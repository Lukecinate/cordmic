using System.Threading.Channels;

namespace CordMic.Core.Transport;

public interface ITransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    ChannelReader<UsbFrame> Frames { get; }
    ValueTask SendAsync(UsbFrame frame, CancellationToken cancellationToken);
    TransportHealth Health { get; }
}
