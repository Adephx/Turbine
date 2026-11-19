using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Turbine.Networking;

internal sealed class NetworkBroker : IAsyncDisposable
{
	private const int HeaderSize = 24, MaxDatagram = 65507, MaxFrame = 65536;
	private const int Invalid = 10022, NotSocket = 10038, NoBuffers = 10055, Refused = 10061, AddressInUse = 10048;
	private const uint Magic = 0x314e4254;

	private enum RequestKind : byte
	{
		Authenticate = 1,
		Register = 2,
		GetDatagramPortal = 3,
		GetRemoteEndpoint = 4,
		SetListening = 5,
		ConnectStream = 6,
		CloseSocket = 7,
		SetMulticastMembership = 8,
		PrepareChild = 9,
		AuthorizeChild = 10,
		SetMulticastOptions = 11,
		SetDatagramPeer = 12
	}

	[Flags]
	internal enum BindOptions : byte
	{
		None = 0,
		ReuseAddress = 1,
		ExclusiveAddressUse = 2
	}

	private readonly IOverlayTransport? transport;
	private readonly uint address, mask;
	private readonly object gate = new();
	private readonly CancellationTokenSource stop = new();
	private readonly HashSet<GameClient> clients = [];
	private readonly Dictionary<(uint App, byte Type, ushort Port), List<Registration>> bindings = [];
	private readonly HashSet<Task> workers = [];
	private readonly SemaphoreSlim streamSlots = new(64, 64);
	private readonly Channel<(uint Source, byte[] Data)> datagrams = Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
	private readonly Task receiver;
	private Task? disposal;

	public event Action<Exception>? Faulted;

	public event Action<int>? ChildAuthorized;

	public bool HasRunningChildren
	{
		get
		{
			lock (gate)
				return clients.Any(client => client.IsChild && client.ChildActive);
		}
	}

	public NetworkBroker() => receiver = Task.CompletedTask;

	public NetworkBroker(IOverlayTransport transport)
	{
		this.transport = transport;
		byte[] bytes = IPAddress.Parse(transport.Address).GetAddressBytes();
		if (bytes.Length != 4 || transport.Netmask == 0)
			throw new IOException("ZeroTier has no usable IPv4 address.");
		address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
		mask = transport.Netmask;
		transport.DatagramReceived += ReceiveDatagram;
		transport.StreamAccepted += AcceptStream;
		receiver = ReceiveDatagramsAsync();
	}

	public GameClient CreateClient(uint appId, bool child = false)
	{
		lock (gate)
		{
			ObjectDisposedException.ThrowIf(stop.IsCancellationRequested, this);
			if (clients.Count >= 32)
				throw new IOException("Too many game sessions are running.");
			var client = new GameClient(this, appId, child);
			clients.Add(client);
			client.Start();
			return client;
		}
	}

	private bool IsUnicast(uint ip) => (ip & mask) == (address & mask) && (ip & ~mask) != 0 && (ip | mask) != uint.MaxValue && ip >> 24 is > 0 and < 224 and not 127;

	private static bool IsMulticast(uint ip) => ip >> 28 == 14;

	private bool IsDestination(uint ip) => ip == uint.MaxValue || IsMulticast(ip) || IsUnicast(ip);

	private void ReceiveDatagram(uint source, byte[] data)
	{
		if (!stop.IsCancellationRequested && IsUnicast(source) && data.Length <= MaxDatagram)
			datagrams.Writer.TryWrite((source, data));
	}

	private async Task ReceiveDatagramsAsync()
	{
		try
		{
			await foreach (var item in datagrams.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
				await DeliverDatagramAsync(item.Source, item.Data).ConfigureAwait(false);
		}
		catch (Exception) when (stop.IsCancellationRequested)
		{
		}
		catch (Exception error)
		{
			ReportFault(error);
		}
	}

	private async Task DeliverDatagramAsync(uint source, byte[] data)
	{
		if (!TryReadHeader(data, 1, out var header) || header.Length != data.Length - HeaderSize || (header.Destination != address && header.Destination != uint.MaxValue && !IsMulticast(header.Destination)))
			return;
		bool multicast = IsMulticast(header.Destination);
		var remote = new RemoteEndpoint(source, header.SourcePort);
		List<UdpPortal> portals = [];
		lock (gate)
		{
			if (!bindings.TryGetValue((header.App, 2, header.DestinationPort), out var registrations))
				return;
			Registration? unicast = null;
			foreach (var registration in registrations)
			{
				if (registration.LoopbackPort == 0 || !registration.Client.Authenticated || (registration.DatagramPeer is { } peer && peer != remote))
					continue;
				if (header.Destination == address)
				{
					unicast ??= registration;
					if (registration.DatagramPeer.HasValue)
					{
						unicast = registration;
						break;
					}
				}
				else if (!multicast || (registration.Groups.Contains(header.Destination) && (source != address || registration.MulticastLoop)))
					AddPortal(registration);
			}
			if (unicast is not null)
				AddPortal(unicast);
		}

		foreach (var portal in portals)
		{
			try
			{
				await portal.Socket.SendToAsync(
					data.AsMemory(HeaderSize),
					SocketFlags.None,
					new IPEndPoint(IPAddress.Loopback, portal.Registration.LoopbackPort),
					portal.Registration.Stop.Token
				).ConfigureAwait(false);
			}
			catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
			{
			}
		}

		void AddPortal(Registration registration)
		{
			try
			{
				portals.Add(GetPortal(registration, remote));
			}
			catch (SocketException)
			{
			}
		}
	}

	private static byte[] Header(byte kind, uint app, ushort sourcePort, ushort destinationPort, uint destination, int length)
	{
		var buffer = new byte[HeaderSize + length];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, Magic);
		buffer[4] = 1;
		buffer[5] = kind;
		BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), app);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), sourcePort);
		BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), destinationPort);
		BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), destination);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), length);
		return buffer;
	}

	private readonly record struct Envelope(uint App, ushort SourcePort, ushort DestinationPort, uint Destination, int Length);

	private static bool TryReadHeader(ReadOnlySpan<byte> data, byte kind, out Envelope header)
	{
		header = default;
		if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic || data[4] != 1 || data[5] != kind || BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != 0)
			return false;
		header = new Envelope(
			BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
			BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
			BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
			BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
			BinaryPrimitives.ReadInt32LittleEndian(data[20..])
		);
		return header.App != 0 && header.SourcePort != 0 && header.DestinationPort != 0 && header.Length is >= 0 and <= MaxDatagram - HeaderSize;
	}

	private Registration Register(GameClient client, ulong id, byte type, ushort requested, ushort loopback, BindOptions options, byte ttl, bool multicastLoop)
	{
		lock (gate)
		{
			if (type is not (1 or 2) || (requested == 0 && loopback == 0) || (options & ~(BindOptions.ReuseAddress | BindOptions.ExclusiveAddressUse)) != 0)
				throw Error(Invalid);
			if (client.Registrations.TryGetValue(id, out var reserved))
			{
				if (reserved.LoopbackPort != 0 || loopback == 0 || reserved.Type != type || reserved.Port != requested || reserved.Options != options)
					throw Error(Invalid);
				reserved.MulticastTtl = ttl;
				reserved.MulticastLoop = multicastLoop;
				reserved.LoopbackPort = loopback;
				return reserved;
			}
			if (client.Registrations.Count >= 1024)
				throw Error(NoBuffers);
			ushort port = requested == 0 ? loopback : requested;
			if (requested == 0)
			{
				for (int attempts = 0; bindings.ContainsKey((client.AppId, type, port)); attempts++)
				{
					if (attempts >= 16384)
						throw Error(NoBuffers);
					port = (ushort)(49152 + ((port - 49152 + 1) & 16383));
				}
			}
			var key = (client.AppId, type, port);
			if (!bindings.TryGetValue(key, out var registrations))
				bindings.Add(key, registrations = []);
			else if (options != BindOptions.ReuseAddress || registrations.Any(existing => existing.Options != BindOptions.ReuseAddress))
				throw Error(AddressInUse);
			var registration = new Registration(client, id, type, port, loopback, options, ttl, multicastLoop);
			registrations.Add(registration);
			client.Registrations.Add(id, registration);
			return registration;
		}
	}

	private Registration FindRegistration(GameClient client, ulong id, byte type)
	{
		if (!client.Registrations.TryGetValue(id, out var registration) || registration.LoopbackPort == 0 || registration.Type != type)
			throw Error(NotSocket);
		return registration;
	}

	private UdpPortal GetPortal(Registration registration, RemoteEndpoint remote, bool localOnly = false)
	{
		if (remote.Address == (address | ~mask))
			remote = remote with
			{
				Address = uint.MaxValue
			};
		if (registration.Stop.IsCancellationRequested)
			throw Error(NotSocket);
		if (remote.Port == 0 || !IsDestination(remote.Address) || (localOnly && !IsMulticast(remote.Address)))
			throw Error(Invalid);
		if (registration.Portals.TryGetValue((remote, localOnly), out var existing))
			return existing;
		if (registration.Portals.Count >= 128 || registration.Client.Endpoints.Count >= 4096)
			throw Error(NoBuffers);
		var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		try
		{
			socket.ExclusiveAddressUse = true;
			socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			var portal = new UdpPortal(registration, remote, localOnly, socket);
			registration.Portals.Add((remote, localOnly), portal);
			registration.Client.Endpoints.Add((2, portal.Port), remote);
			StartWorker(() => PortalLoopAsync(portal));
			return portal;
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private async Task PortalLoopAsync(UdpPortal portal)
	{
		byte[] buffer = new byte[MaxDatagram + 1];
		Registration registration = portal.Registration;
		var cancel = registration.Stop.Token;
		try
		{
			while (true)
			{
				SocketReceiveFromResult received;
				try
				{
					received = await portal.Socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancel).ConfigureAwait(false);
				}
				catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
				{
					continue;
				}

				if (received.RemoteEndPoint is not IPEndPoint sender || !sender.Address.Equals(IPAddress.Loopback) || sender.Port != registration.LoopbackPort || received.ReceivedBytes > MaxDatagram - HeaderSize)
					continue;
				var remote = portal.Remote;
				byte[] frame = Header(1, registration.Client.AppId, registration.Port, remote.Port, remote.Address, received.ReceivedBytes);
				buffer.AsSpan(0, received.ReceivedBytes).CopyTo(frame.AsSpan(HeaderSize));
				bool multicast = IsMulticast(remote.Address);
				bool sendRemote = remote.Address != address && !portal.LocalOnly;
				if (remote.Address == address || remote.Address == uint.MaxValue || multicast)
					await DeliverDatagramAsync(address, frame).ConfigureAwait(false);
				if (sendRemote)
					await transport!.SendDatagramAsync(multicast ? uint.MaxValue : remote.Address, frame, cancel).ConfigureAwait(false);
			}
		}
		catch (Exception) when (cancel.IsCancellationRequested || stop.IsCancellationRequested)
		{
		}
		catch (IOException error)
		{
			ReportFault(error);
		}
	}

	private void AcceptStream(uint source, Stream stream)
	{
		lock (gate)
		{
			if (!stop.IsCancellationRequested && IsUnicast(source) && streamSlots.Wait(0))
			{
				StartWorker(async () =>
				{
					try
					{
						await AcceptStreamAsync(source, stream).ConfigureAwait(false);
					}
					finally
					{
						stream.Dispose();
						streamSlots.Release();
					}
				});
				return;
			}
		}

		stream.Dispose();
	}

	private async Task AcceptStreamAsync(uint source, Stream stream)
	{
		using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
		handshake.CancelAfter(TimeSpan.FromSeconds(10));
		byte[] bytes = new byte[HeaderSize];
		await stream.ReadExactlyAsync(bytes, handshake.Token).ConfigureAwait(false);
		if (!TryReadHeader(bytes, 2, out var header) || header.Length != 0 || header.Destination != address)
			return;
		Registration? registration;
		lock (gate)
		{
			bindings.TryGetValue((header.App, 1, header.DestinationPort), out var registrations);
			registration = registrations?.FirstOrDefault(entry => entry.Listening && entry.Client.Authenticated && entry.Client.Streams < 64);
			if (registration is not null)
				registration.Client.Streams++;
		}

		if (registration is null)
		{
			await WriteStatusAsync(stream, Refused, handshake.Token).ConfigureAwait(false);
			return;
		}

		var client = registration.Client;
		using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(client.Stop, stop.Token);
		using var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		ushort sourcePort = 0;
		try
		{
			local.NoDelay = true;
			local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			sourcePort = (ushort)((IPEndPoint)local.LocalEndPoint!).Port;
			lock (gate)
				client.Endpoints.Add((1, sourcePort), new RemoteEndpoint(source, header.SourcePort));
			try
			{
				await local.ConnectAsync(new IPEndPoint(IPAddress.Loopback, registration.LoopbackPort), handshake.Token).ConfigureAwait(false);
			}
			catch (Exception error) when (error is SocketException or OperationCanceledException)
			{
				await WriteStatusAsync(stream, Refused, handshake.Token).ConfigureAwait(false);
				return;
			}

			await WriteStatusAsync(stream, 0, handshake.Token).ConfigureAwait(false);
			handshake.CancelAfter(Timeout.InfiniteTimeSpan);
			await RelayAsync(local, stream, connectionStop.Token).ConfigureAwait(false);
		}
		finally
		{
			lock (gate)
			{
				if (sourcePort != 0)
					client.Endpoints.Remove((1, sourcePort));
				client.Streams--;
			}
		}
	}

	private async Task<ushort> ConnectStreamAsync(GameClient client, ulong id, RemoteEndpoint remote, bool nonblocking, CancellationToken cancellationToken)
	{
		Registration registration;
		lock (gate)
		{
			registration = FindRegistration(client, id, 1);
			if (!IsUnicast(remote.Address) || remote.Port == 0 || registration.Listening)
				throw Error(Invalid);
			if (registration.Connecting || client.Streams >= 64 || !streamSlots.Wait(0))
				throw Error(NoBuffers);
			registration.Connecting = true;
			client.Streams++;
		}

		Stream? overlay = null;
		Socket? listener = null;
		ushort gatewayPort = 0;
		bool handedOff = false;
		var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, registration.Stop.Token, stop.Token);
		pending.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			if (!nonblocking)
				overlay = await OpenRemoteAsync(registration, remote, pending.Token).ConfigureAwait(false);
			listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			listener.ExclusiveAddressUse = true;
			listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			listener.Listen(1);
			gatewayPort = (ushort)((IPEndPoint)listener.LocalEndPoint!).Port;
			lock (gate)
				client.Endpoints.Add((1, gatewayPort), remote);
			var gateway = listener;
			var stream = overlay;
			ushort port = gatewayPort;
			StartWorker(async () =>
			{
				try
				{
					using (gateway)
					using (pending)
					{
						Socket local;
						while (true)
						{
							local = await gateway.AcceptAsync(pending.Token).ConfigureAwait(false);
							if (local.RemoteEndPoint is IPEndPoint endpoint && endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == registration.LoopbackPort)
								break;
							local.Dispose();
						}

						using (local)
						{
							local.NoDelay = true;
							try
							{
								stream ??= await OpenRemoteAsync(registration, remote, pending.Token).ConfigureAwait(false);
								pending.CancelAfter(Timeout.InfiniteTimeSpan);
								await RelayAsync(local, stream, pending.Token).ConfigureAwait(false);
							}
							catch
							{
								local.LingerState = new LingerOption(true, 0);
								throw;
							}
						}
					}
				}
				finally
				{
					stream?.Dispose();
					lock (gate)
					{
						client.Endpoints.Remove((1, port));
						registration.Connecting = false;
						client.Streams--;
					}

					streamSlots.Release();
				}
			});
			handedOff = true;
			return gatewayPort;
		}
		catch (Exception error) when (error is TimeoutException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
		{
			throw Error(10060);
		}
		finally
		{
			if (!handedOff)
			{
				overlay?.Dispose();
				listener?.Dispose();
				pending.Dispose();
				lock (gate)
				{
					if (gatewayPort != 0)
						client.Endpoints.Remove((1, gatewayPort));
					registration.Connecting = false;
					client.Streams--;
				}

				streamSlots.Release();
			}
		}
	}

	private async Task<Stream> OpenRemoteAsync(Registration registration, RemoteEndpoint remote, CancellationToken token)
	{
		Stream stream = remote.Address == address ? await OpenLocalStreamAsync(token).ConfigureAwait(false) : await transport!.ConnectAsync(remote.Address, token).ConfigureAwait(false);
		try
		{
			await stream.WriteAsync(Header(2, registration.Client.AppId, registration.Port, remote.Port, remote.Address, 0), token).ConfigureAwait(false);
			byte[] status = new byte[4];
			await stream.ReadExactlyAsync(status, token).ConfigureAwait(false);
			int error = BinaryPrimitives.ReadInt32LittleEndian(status);
			if (error != 0)
				throw Error(error is >= 10000 and <= 11031 ? error : Refused);
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	private async Task<Stream> OpenLocalStreamAsync(CancellationToken token)
	{
		using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		listener.Listen(1);
		var outgoing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		Socket? incoming = null;
		try
		{
			Task connect = outgoing.ConnectAsync(listener.LocalEndPoint!, token).AsTask();
			incoming = await listener.AcceptAsync(token).ConfigureAwait(false);
			await connect.ConfigureAwait(false);
			AcceptStream(address, new LocalStream(incoming));
			incoming = null;
			return new LocalStream(outgoing);
		}
		catch
		{
			incoming?.Dispose();
			outgoing.Dispose();
			throw;
		}
	}

	private sealed class LocalStream(Socket socket) : NetworkStream(socket, true), IOverlayHalfClose
	{
		public void ShutdownWrite() => Socket.Shutdown(SocketShutdown.Send);
	}

	private static async Task RelayAsync(Socket local, Stream remote, CancellationToken cancellationToken)
	{
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		using var network = new NetworkStream(local, false);

		async Task CopyAsync(Stream from, Stream to, Action shutdown)
		{
			try
			{
				await from.CopyToAsync(to, 32768, cancel.Token).ConfigureAwait(false);
				shutdown();
			}
			catch
			{
				cancel.Cancel();
				throw;
			}
		}

		Task upstream = CopyAsync(network, remote, () =>
		{
			if (remote is IOverlayHalfClose halfClose)
				halfClose.ShutdownWrite();
		});
		Task downstream = CopyAsync(remote, network, () => local.Shutdown(SocketShutdown.Send));
		await Task.WhenAll(upstream, downstream).ConfigureAwait(false);
	}

	private async Task<byte[]> RequestAsync(GameClient client, byte[] bytes, CancellationToken cancellationToken)
	{
		var kind = (RequestKind)bytes[0];
		if (transport is null && kind is not (RequestKind.PrepareChild or RequestKind.AuthorizeChild))
			throw Error(Invalid);
		int expectedLength = kind switch
		{
			RequestKind.Register => 17,
			RequestKind.GetDatagramPortal => 16,
			RequestKind.GetRemoteEndpoint => 4,
			RequestKind.SetListening => 10,
			RequestKind.ConnectStream => bytes.Length == 15 ? 15 : 16,
			RequestKind.CloseSocket => 9,
			RequestKind.SetMulticastMembership => 14,
			RequestKind.SetMulticastOptions => 11,
			RequestKind.SetDatagramPeer => 15,
			RequestKind.PrepareChild => 1,
			RequestKind.AuthorizeChild => 21,
			_ => 0
		};
		if (bytes.Length != expectedLength || expectedLength == 0)
			throw new InvalidDataException("Invalid networking request.");
		byte[] reply = [];
		ulong id = bytes.Length >= 9 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(1)) : 0;
		switch (kind)
		{
			case RequestKind.Register:
				if (bytes[16] > 1)
					throw Error(Invalid);
				var registration = Register(
					client,
					id,
					bytes[9],
					BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)),
					BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)),
					(BindOptions)bytes[14],
					bytes[15],
					bytes[16] == 1
				);
				reply = new byte[2];
				BinaryPrimitives.WriteUInt16LittleEndian(reply, registration.Port);
				break;
			case RequestKind.GetDatagramPortal:
				lock (gate)
				{
					if (bytes[15] > 1)
						throw Error(Invalid);
					var portal = GetPortal(
						FindRegistration(client, id, 2),
						new RemoteEndpoint(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(13))),
						bytes[15] == 1
					);
					reply = new byte[2];
					BinaryPrimitives.WriteUInt16LittleEndian(reply, portal.Port);
				}

				break;
			case RequestKind.GetRemoteEndpoint:
				lock (gate)
				{
					if (!client.Endpoints.TryGetValue((bytes[1], BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))), out var endpoint))
						throw Error(Invalid);
					reply = new byte[6];
					BinaryPrimitives.WriteUInt32LittleEndian(reply, endpoint.Address);
					BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(4), endpoint.Port);
				}

				break;
			case RequestKind.SetListening:
				lock (gate)
				{
					if (bytes[9] > 1)
						throw Error(Invalid);
					FindRegistration(client, id, 1).Listening = bytes[9] == 1;
				}

				break;
			case RequestKind.ConnectStream:
				if (bytes.Length == 16 && bytes[15] > 1)
					throw Error(Invalid);
				ushort port = await ConnectStreamAsync(
					client,
					id,
					new RemoteEndpoint(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(13))),
					bytes.Length == 16 && bytes[15] == 1,
					cancellationToken
				).ConfigureAwait(false);
				reply = new byte[2];
				BinaryPrimitives.WriteUInt16LittleEndian(reply, port);
				break;
			case RequestKind.CloseSocket:
				lock (gate)
					if (client.Registrations.TryGetValue(id, out var closing))
						RemoveRegistration(closing);
				break;
			case RequestKind.SetDatagramPeer:
				lock (gate)
				{
					var socket = FindRegistration(client, id, 2);
					var peer = new RemoteEndpoint(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(13)));
					if (peer != default && peer.Address >> 24 != 127 && (peer.Port == 0 || !IsDestination(peer.Address)))
						throw Error(Invalid);
					socket.DatagramPeer = peer == default ? null : peer;
				}
				break;
			case RequestKind.SetMulticastOptions:
				lock (gate)
				{
					if (bytes[10] > 1)
						throw Error(Invalid);
					var receiver = FindRegistration(client, id, 2);
					receiver.MulticastTtl = bytes[9];
					receiver.MulticastLoop = bytes[10] == 1;
				}
				break;
			case RequestKind.SetMulticastMembership:
				lock (gate)
				{
					var member = FindRegistration(client, id, 2);
					uint group = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9));
					if (!IsMulticast(group) || bytes[13] > 1)
						throw Error(Invalid);
					if (bytes[13] == 0)
						member.Groups.Remove(group);
					else
					{
						if (member.Groups.Count >= 64 && !member.Groups.Contains(group))
							throw Error(NoBuffers);
						member.Groups.Add(group);
					}
				}

				break;
			case RequestKind.PrepareChild:
				GameClient child;
				string childKey;
				lock (gate)
				{
					if (client.PendingChildren.Count >= 16)
						throw Error(NoBuffers);
					child = CreateClient(client.AppId, true);
					child.PreparedBy = client;
					childKey = Convert.ToHexString(child.Token);
					client.PendingChildren.Add(childKey, child);
					byte[] name = Encoding.UTF8.GetBytes(child.PipeName);
					reply = new byte[2 + name.Length + 16];
					BinaryPrimitives.WriteUInt16LittleEndian(reply, (ushort)name.Length);
					name.CopyTo(reply, 2);
					child.Token.CopyTo(reply, 2 + name.Length);
				}

				StartWorker(async () =>
				{
					try
					{
						await Task.Delay(TimeSpan.FromSeconds(30), child.Stop).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						return;
					}

					lock (gate)
					{
						if (!client.PendingChildren.Remove(childKey))
							return;
						child.PreparedBy = null;
					}

					await child.DisposeAsync().ConfigureAwait(false);
				});
				break;
			case RequestKind.AuthorizeChild:
				string key = Convert.ToHexString(bytes.AsSpan(1, 16));
				uint childPid = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(17));
				GameClient prepared;
				lock (gate)
				{
					if (!client.PendingChildren.TryGetValue(key, out prepared!))
						throw Error(10013);
					if (childPid != 0 && (childPid > int.MaxValue || !IsChildOf(childPid, (uint)client.ProcessId)))
						throw Error(10013);

					client.PendingChildren.Remove(key);
					prepared.PreparedBy = null;
					if (childPid != 0)
					{
						try
						{
							prepared.TrackChild((int)childPid);
						}
						catch
						{
							_ = prepared.DisposeAsync();
							throw;
						}
					}
				}

				if (childPid == 0)
					await prepared.DisposeAsync().ConfigureAwait(false);
				break;
		}

		return reply;
	}

	private static SocketException Error(int code) => new(code);

	private static async Task WriteStatusAsync(Stream stream, int error, CancellationToken token)
	{
		byte[] bytes = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(bytes, error);
		await stream.WriteAsync(bytes, token).ConfigureAwait(false);
	}

	private void RemoveRegistration(Registration registration)
	{
		registration.Stop.Cancel();
		foreach (var portal in registration.Portals.Values)
		{
			portal.Socket.Dispose();
			registration.Client.Endpoints.Remove((2, portal.Port));
		}

		registration.Portals.Clear();
		registration.Groups.Clear();
		registration.Stop.Dispose();
		registration.Client.Registrations.Remove(registration.Id);
		var key = (registration.Client.AppId, registration.Type, registration.Port);
		var registrations = bindings[key];
		registrations.Remove(registration);
		if (registrations.Count == 0)
			bindings.Remove(key);
	}

	private void RemoveClient(GameClient client)
	{
		lock (gate)
		{
			foreach (var registration in client.Registrations.Values.ToArray())
				RemoveRegistration(registration);
			client.Endpoints.Clear();
			if (client.PreparedBy is { } parent)
				parent.PendingChildren.Remove(Convert.ToHexString(client.Token));
			foreach (var pending in client.PendingChildren.Values.ToArray())
				_ = pending.DisposeAsync();
			client.PendingChildren.Clear();
			clients.Remove(client);
		}
	}

	private void StartWorker(Func<Task> work)
	{
		lock (gate)
		{
			var task = Task.Run(async () =>
			{
				try
				{
					await work().ConfigureAwait(false);
				}
				catch (Exception error) when (error is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
				{
				}
				catch (Exception error)
				{
					ReportFault(error);
				}
			});
			workers.Add(task);
			_ = task.ContinueWith(completed =>
			{
				lock (gate)
					workers.Remove(completed);
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
	}

	private void ReportFault(Exception error)
	{
		try
		{
			Faulted?.Invoke(error);
		}
		catch (Exception)
		{
		}
	}

	private static bool IsChildOf(uint child, uint parent)
	{
		using var snapshot = CreateToolhelp32Snapshot(2, 0);
		if (snapshot.IsInvalid)
			return false;
		var entry = new ProcessEntry
		{
			Size = (uint)Marshal.SizeOf<ProcessEntry>(),
			Executable = ""
		};
		if (!Process32FirstW(snapshot, ref entry))
			return false;
		do
		{
			if (entry.ProcessId == child)
				return entry.ParentProcessId == parent;
		}
		while (Process32NextW(snapshot, ref entry));
		return false;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry
	{
		public uint Size, Usage, ProcessId;
		public nuint Heap;
		public uint Module, Threads, ParentProcessId;
		public int BasePriority;
		public uint Flags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string Executable;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

	public ValueTask DisposeAsync()
	{
		lock (gate)
			return new ValueTask(disposal ??= DisposeCoreAsync());
	}

	private async Task DisposeCoreAsync()
	{
		stop.Cancel();
		if (transport is not null)
		{
			transport.DatagramReceived -= ReceiveDatagram;
			transport.StreamAccepted -= AcceptStream;
		}
		datagrams.Writer.TryComplete();
		GameClient[] active;
		lock (gate)
			active = clients.ToArray();
		try
		{
			await Task.WhenAll(active.Select(client => client.DisposeAsync().AsTask())).ConfigureAwait(false);
		}
		finally
		{
			await receiver.ConfigureAwait(false);
			Task[] pending;
			lock (gate)
				pending = workers.ToArray();
			await Task.WhenAll(pending).ConfigureAwait(false);
			streamSlots.Dispose();
			stop.Dispose();
		}
	}

	internal readonly record struct RemoteEndpoint(uint Address, ushort Port);

	internal sealed class Registration(GameClient client, ulong id, byte type, ushort port, ushort loopbackPort, BindOptions options, byte ttl, bool multicastLoop)
	{
		public GameClient Client { get; } = client;

		public ulong Id { get; } = id;

		public byte Type { get; } = type;

		public ushort Port { get; } = port;

		public ushort LoopbackPort { get; set; } = loopbackPort;

		public BindOptions Options { get; } = options;

		public byte MulticastTtl { get; set; } = ttl;

		public bool MulticastLoop { get; set; } = multicastLoop;

		public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(client.Stop);

		public RemoteEndpoint? DatagramPeer { get; set; }

		public Dictionary<(RemoteEndpoint Remote, bool LocalOnly), UdpPortal> Portals { get; } = [];

		public HashSet<uint> Groups { get; } = [];

		public bool Listening, Connecting;
	}

	internal sealed class UdpPortal(Registration registration, RemoteEndpoint remote, bool localOnly, Socket socket)
	{
		public Registration Registration { get; } = registration;

		public RemoteEndpoint Remote { get; } = remote;

		public bool LocalOnly { get; } = localOnly;

		public Socket Socket { get; } = socket;

		public ushort Port { get; } = (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
	}

	internal sealed class GameClient : IAsyncDisposable
	{
		private readonly NetworkBroker owner;
		private Task? pipeTask, disposal;
		private volatile NamedPipeServerStream? pipe;
		private readonly CancellationTokenSource stop = new();
		private int processId;
		private Process? childProcess;
		private volatile bool authenticated;
		private volatile bool handshakeSucceeded;
		private readonly object disposalGate = new();

		internal CancellationToken Stop { get; }

		internal Dictionary<ulong, Registration> Registrations { get; } = [];

		internal Dictionary<(byte, ushort), RemoteEndpoint> Endpoints { get; } = [];

		internal uint AppId
		{
			get;
		}

		internal bool IsChild
		{
			get;
		}

		internal bool ChildPending
		{
			get; private set;
		}

		internal bool ChildActive
		{
			get
			{
				try
				{
					return ChildPending || childProcess is { HasExited: false };
				}
				catch (InvalidOperationException)
				{
					return false;
				}
			}
		}

		internal int ProcessId => Volatile.Read(ref processId);

		internal GameClient? PreparedBy
		{
			get; set;
		}

		internal Dictionary<string, GameClient> PendingChildren { get; } = [];

		internal int Streams;

		internal string PipeName { get; } = "Turbine-" + Guid.NewGuid().ToString("N");

		internal byte[] Token { get; } = RandomNumberGenerator.GetBytes(16);

		internal bool Authenticated => authenticated && !Stop.IsCancellationRequested;

		internal bool HandshakeSucceeded => handshakeSucceeded;

		internal GameClient(NetworkBroker owner, uint appId, bool child)
		{
			this.owner = owner;
			Stop = stop.Token;
			AppId = appId;
			IsChild = ChildPending = child;
		}

		internal void Start() => pipeTask = PipeLoopAsync();

		internal void Authorize(int pid)
		{
			if (pid <= 0 || Interlocked.CompareExchange(ref processId, pid, 0) != 0)
				throw new InvalidOperationException("The game session is already authorized.");
		}

		internal void TrackChild(int pid)
		{
			Process process;
			try
			{
				process = Process.GetProcessById(pid);
			}
			catch (ArgumentException)
			{
				throw Error(10013);
			}

			try
			{
				if (process.HasExited)
					throw Error(10013);
				Authorize(pid);
				childProcess = process;
				ChildPending = false;
				owner.ChildAuthorized?.Invoke(pid);
				process.Exited += ChildExited;
				process.EnableRaisingEvents = true;
				if (!ChildActive)
					_ = DisposeAsync();
			}
			catch
			{
				process.Dispose();
				throw;
			}
		}

		private void ChildExited(object? sender, EventArgs e) => _ = DisposeAsync();

		private async Task PipeLoopAsync()
		{
			try
			{
				while (!Stop.IsCancellationRequested)
				{
					using var connection = new NamedPipeServerStream(
						PipeName,
						PipeDirection.InOut,
						1,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
						MaxFrame + 4,
						MaxFrame + 4
					);
					pipe = connection;
					await connection.WaitForConnectionAsync(Stop).ConfigureAwait(false);
					using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
					authTimeout.CancelAfter(TimeSpan.FromSeconds(5));
					try
					{
						byte[] request = await ReadFrameAsync(connection, authTimeout.Token).ConfigureAwait(false);
						if (request.Length != 17 || request[0] != (byte)RequestKind.Authenticate || !CryptographicOperations.FixedTimeEquals(request.AsSpan(1), Token) || !GetNamedPipeClientProcessId(connection.SafePipeHandle, out uint pid) || pid != Volatile.Read(ref processId) || pid == 0)
						{
							await ReplyAsync(connection, 10013, [], authTimeout.Token).ConfigureAwait(false);
							continue;
						}

						byte[] reply = new byte[8];
						BinaryPrimitives.WriteUInt32LittleEndian(reply, owner.address);
						BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), owner.mask);
						authenticated = true;
						handshakeSucceeded = true;
						await ReplyAsync(connection, 0, reply, authTimeout.Token).ConfigureAwait(false);
					}
					catch (Exception error) when (!Stop.IsCancellationRequested && error is IOException or InvalidDataException or OperationCanceledException)
					{
						authenticated = false;
						handshakeSucceeded = false;
						continue;
					}

					while (!Stop.IsCancellationRequested)
					{
						byte[] request = await ReadFrameAsync(connection, Stop).ConfigureAwait(false);
						try
						{
							byte[] response = await owner.RequestAsync(this, request, Stop).ConfigureAwait(false);
							await ReplyAsync(connection, 0, response, Stop).ConfigureAwait(false);
						}
						catch (SocketException error)
						{
							await ReplyAsync(connection, error.NativeErrorCode, [], Stop).ConfigureAwait(false);
						}
						catch (IOException)
						{
							await ReplyAsync(connection, Refused, [], Stop).ConfigureAwait(false);
						}
					}
				}
			}
			catch (Exception) when (Stop.IsCancellationRequested)
			{
			}
			catch (Exception error) when (error is IOException or InvalidDataException or SocketException)
			{
			}
			finally
			{
				authenticated = false;
				stop.Cancel();
				owner.RemoveClient(this);
			}
		}

		private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
		{
			byte[] header = new byte[4];
			await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
			uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
			if (length is 0 or > MaxFrame)
				throw new InvalidDataException("Invalid networking frame length.");
			byte[] data = new byte[(int)length];
			await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
			return data;
		}

		private static async Task ReplyAsync(Stream stream, int error, byte[] data, CancellationToken token)
		{
			byte[] bytes = new byte[data.Length + 8];
			BinaryPrimitives.WriteInt32LittleEndian(bytes, data.Length + 4);
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), error);
			data.CopyTo(bytes, 8);
			await stream.WriteAsync(bytes, token).ConfigureAwait(false);
		}

		public ValueTask DisposeAsync()
		{
			lock (disposalGate)
				return new ValueTask(disposal ??= DisposeCoreAsync());
		}

		private async Task DisposeCoreAsync()
		{
			// Yield before pipe shutdown to keep broker callbacks outside the disposal lock.
			await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
			try
			{
				stop.Cancel();
				pipe?.Dispose();
				if (pipeTask is not null)
					await pipeTask.ConfigureAwait(false);
			}
			finally
			{
				owner.RemoveClient(this);
				if (childProcess is { } child)
				{
					child.Exited -= ChildExited;
					child.Dispose();
					childProcess = null;
				}
				stop.Dispose();
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
	}
}
