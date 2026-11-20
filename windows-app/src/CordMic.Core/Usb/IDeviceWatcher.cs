namespace CordMic.Core.Usb;

public enum WatchedDeviceKind { Unknown, UsbDevice, WinUsbInterface, Accessory }

public readonly record struct DeviceWatchEvent(bool Arrived, string InstanceId, WatchedDeviceKind Kind, int VendorId, int ProductId);

public interface IDeviceWatcher : IDisposable
{
    event Action<DeviceWatchEvent>? Changed;
    void Start();
    void Stop();
}

/// <summary>Feeds scripted events; a 3 s rescan is represented as a later Arrive for missed notifications.</summary>
public sealed class SimDeviceWatch : IDeviceWatcher
{
    private readonly Queue<DeviceWatchEvent> _q = new();
    public event Action<DeviceWatchEvent>? Changed;
    public bool Running { get; private set; }
    public int Dispatched { get; private set; }

    public void Enqueue(DeviceWatchEvent ev) => _q.Enqueue(ev);

    public void Start() => Running = true;
    public void Stop() => Running = false;

    public int Pump()
    {
        int n = 0;
        while (_q.Count > 0)
        {
            var ev = _q.Dequeue();
            Dispatched++;
            n++;
            Changed?.Invoke(ev);
        }
        return n;
    }

    public void Dispose() => Stop();
}
