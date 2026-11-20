namespace CordMic.Core.Transport;

public enum SimDeviceKind { Unknown, Accessory, WinUsb, Tether, Midi }

public readonly record struct SimDeviceEvent(bool Arrived, string InstanceId, SimDeviceKind Kind, long AtMs);

/// <summary>Scripted device arrival/removal, including storms and out-of-order notifications.</summary>
public sealed class SimDeviceWatcher
{
    private readonly List<SimDeviceEvent> _events = new();
    private int _i;

    public int Count => _events.Count;
    public int Remaining => Math.Max(0, _events.Count - _i);

    public void Arrive(string id, SimDeviceKind kind, long atMs = 0) =>
        _events.Add(new SimDeviceEvent(true, id, kind, atMs));

    public void Remove(string id, SimDeviceKind kind, long atMs = 0) =>
        _events.Add(new SimDeviceEvent(false, id, kind, atMs));

    public void Storm(string prefix, int count, int perSecond, long startMs = 0)
    {
        int spacing = Math.Max(1, 1000 / Math.Max(perSecond, 1));
        for (int i = 0; i < count; i++)
        {
            string id = prefix + i;
            Arrive(id, SimDeviceKind.Accessory, startMs + i * spacing);
            Remove(id, SimDeviceKind.Accessory, startMs + i * spacing + spacing / 2);
        }
    }

    public bool TryNext(out SimDeviceEvent ev)
    {
        if (_i >= _events.Count) { ev = default; return false; }
        ev = _events[_i++];
        return true;
    }

    public IReadOnlyList<SimDeviceEvent> Snapshot() => _events.ToArray();
}
