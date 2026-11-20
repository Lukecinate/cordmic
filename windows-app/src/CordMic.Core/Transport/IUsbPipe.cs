namespace CordMic.Core.Transport;

public interface IUsbPipe : IAsyncDisposable
{
    /// <summary>Instance identity; a re-plug with a new id must not reuse this pipe.</summary>
    string InstanceId { get; }

    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

public sealed class UsbPipeException : Exception
{
    public UsbPipeException(string code, string message) : base(message) => Code = code;
    public string Code { get; }

    public static UsbPipeException Disconnected() => new("disconnected", "device not connected");
    public static UsbPipeException Stall() => new("stall", "pipe stalled");
}
