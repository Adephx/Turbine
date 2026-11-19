using System.IO;

namespace Turbine.Networking;

internal interface IOverlayTransport : IAsyncDisposable
{
	event Action<uint, byte[]>? DatagramReceived;

	event Action<uint, Stream>? StreamAccepted;

	event Action<Exception>? Faulted;

	string Address
	{
		get;
	}

	uint Netmask
	{
		get;
	}

	Task StartAsync(ulong networkId, string identity, CancellationToken cancellationToken);

	ValueTask SendDatagramAsync(uint destinationIpv4, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

	Task<Stream> ConnectAsync(uint destinationIpv4, CancellationToken cancellationToken);
}

internal interface IOverlayHalfClose
{
	void ShutdownWrite();
}
