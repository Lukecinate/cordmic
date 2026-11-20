namespace CordMic.Core.Transport;

public sealed class UsbTransportOptions
{
    public int AudioChannelCap { get; init; } = 64;
    public int ControlChannelCap { get; init; } = 16;
    public int WriterQueueCap { get; init; } = 16;
    public int ReadTimeoutMs { get; init; } = 500;
    public int WriteTimeoutMs { get; init; } = 200;
    public int KeepaliveMs { get; init; } = 250;
    public int SilenceLostMs { get; init; } = 3000;
    public int ConsecutiveReadTimeoutsToSuspect { get; init; } = 3;
    public int ConsecutiveWriteFailsToLose { get; init; } = 5;
    public string AppVersion { get; init; } = "0.8.0";
}
