namespace CordMic.Core.Usb;

/// <summary>Platform seam for AOA vendor requests 51–53. The simulator implements this; WinUSB is in the app.</summary>
public interface IAoaChannel
{
    /// <summary>Performs one control transfer. Returns the number of bytes moved. Honours the setup timeout.</summary>
    Task<int> TransferAsync(AoaProtocol.ControlSetup setup, byte[] buffer, CancellationToken cancellationToken);
}

public enum AoaHandshakeStatus
{
    Ok = 0,
    ProtocolFailed = 1,
    Unsupported = 2,
    StartFailed = 3,
    Timeout = 4,
    Cancelled = 5,
}

public readonly record struct AoaHandshakeResult(AoaHandshakeStatus Status, int ProtocolVersion, string Detail);
