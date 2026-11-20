namespace CordMic.Core.Usb;

/// <summary>
/// WinUSB pipe policy values from the public WinUSB API (PIPE_TRANSFER_TIMEOUT, RAW_IO, AUTO_CLEAR_STALL).
/// Applied by the Windows pipe in the app; listed here so tests can lock the intended defaults.
/// </summary>
public static class WinUsbPipePolicy
{
    public const int PipeTransferTimeout = 0x03;
    public const int RawIo = 0x07;
    public const int AutoClearStall = 0x08;

    public const int BulkReadTimeoutMs = 500;
    public const int BulkWriteTimeoutMs = 200;
    public const int OutstandingReads = 3;
    public const int ReadBufferBytes = 16 * 1024;
    public const bool RawIoEnabled = false;
    public const bool AutoClearStallEnabled = true;

    public static IReadOnlyList<(int Policy, int Value)> DefaultPolicies() => new[]
    {
        (PipeTransferTimeout, BulkReadTimeoutMs),
        (RawIo, RawIoEnabled ? 1 : 0),
        (AutoClearStall, AutoClearStallEnabled ? 1 : 0),
    };
}
