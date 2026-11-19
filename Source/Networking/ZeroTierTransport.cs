using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Turbine.Networking;

internal sealed class ZeroTierTransport : IOverlayTransport
{
	private const ushort Port = 49763;
	private const int IdentityBufferSize = 384;
	private static readonly SemaphoreSlim NodeLease = new(1, 1);
	private static readonly object NativeGate = new();
	private static string? nodeIdentity;
	private readonly SemaphoreSlim lifecycle = new(1, 1);
	private readonly CancellationTokenSource stop = new();
	private readonly HashSet<NativeSocket> sockets = [];
	private readonly object disposeGate = new();
	private Task? disposeTask;
	private Task[] receivers = [];
	private NativeSocket? udp;
	private ulong network;
	private uint broadcast;
	private uint localAddress, subnet, subnetMask;
	private bool leased, joined, attempted, disposed, ready;

	public event Action<uint, byte[]>? DatagramReceived;

	public event Action<uint, Stream>? StreamAccepted;

	public event Action<Exception>? Faulted;

	public string Address { get; private set; } = "";

	public uint Netmask => subnetMask;

	private ulong NodeId
	{
		get; set;
	}

	internal static string CreateIdentity()
	{
		var buffer = new byte[IdentityBufferSize];
		uint length = (uint)buffer.Length;
		Check(Native.zts_id_new(buffer, ref length));
		if (length >= buffer.Length)
			throw new IOException("Could not create a ZeroTier identity.");
		var identity = Encoding.ASCII.GetString(buffer, 0, (int)length);
		ValidateIdentity(identity);
		return identity;
	}

	internal static void ValidateIdentity(string identity)
	{
		if (string.IsNullOrEmpty(identity))
			throw new IOException("The ZeroTier identity is missing.");
		var parts = identity.Split(':');
		if (parts.Length != 4 || parts[0].Length != 10 || parts[1] != "0" || parts[2].Length != 128 || parts[3].Length != 128 ||
			!parts[0].All(char.IsAsciiHexDigit) || !parts[2].All(char.IsAsciiHexDigit) || !parts[3].All(char.IsAsciiHexDigit) ||
			Native.zts_id_pair_is_valid(IdentityBytes(identity), IdentityBufferSize) != 1)
			throw new IOException("The saved ZeroTier identity is invalid. Restore a valid copy to keep this node's authorization.");
	}

	private static byte[] IdentityBytes(string identity)
	{
		// libzt 1.8.4 requires a fixed 384-byte, zero-terminated identity buffer.
		var buffer = new byte[IdentityBufferSize];
		Encoding.ASCII.GetBytes(identity, buffer);
		return buffer;
	}

	private static void DeleteNodeDirectory(string directory)
	{
		try
		{
			Directory.Delete(directory);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	public async Task StartAsync(ulong networkId, string identity, CancellationToken cancellationToken)
	{
		if (networkId == 0)
			throw new ArgumentException("Enter a ZeroTier network ID.", nameof(networkId));
		ValidateIdentity(identity);
		await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			if (attempted)
				throw new InvalidOperationException("Create a new transport to reconnect.");
			attempted = true;
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(120));
			bool online = false;
			int networkStatus = -1;
			try
			{
				await NodeLease.WaitAsync(timeout.Token).ConfigureAwait(false);
				leased = true;
				lock (NativeGate)
				{
					if (nodeIdentity is null)
					{
						// An empty home path makes libzt 1.8.4 probe cache paths at the drive root.
						var directory = Directory.CreateTempSubdirectory("Turbine-ZeroTier-").FullName;
						try
						{
							Check(Native.zts_init_from_storage(directory));
							Check(Native.zts_init_from_memory(IdentityBytes(identity), IdentityBufferSize));
							Check(Native.zts_init_allow_id_cache(0));
							Check(Native.zts_init_allow_net_cache(0));
							Check(Native.zts_init_allow_peer_cache(0));
							Check(Native.zts_init_allow_roots_cache(0));
							Check(Native.zts_node_start());
							nodeIdentity = identity;
							AppDomain.CurrentDomain.ProcessExit += (_, _) =>
							{
								lock (NativeGate)
									Native.zts_node_stop();
								DeleteNodeDirectory(directory);
							};
						}
						catch
						{
							DeleteNodeDirectory(directory);
							throw;
						}
					}
					else if (!string.Equals(identity, nodeIdentity, StringComparison.Ordinal))
						throw new InvalidOperationException("Restart Turbine to change its ZeroTier identity.");
				}

				while (true)
				{
					timeout.Token.ThrowIfCancellationRequested();
					lock (NativeGate)
					{
						ulong id = Native.zts_node_get_id();
						if (id is > 0 and <= 0xffffffffff)
							NodeId = id;
						if (Native.zts_node_is_online() == 1)
						{
							online = true;
							break;
						}
					}

					await Task.Delay(100, timeout.Token).ConfigureAwait(false);
				}

				lock (NativeGate)
				{
					network = networkId;
					Check(Native.zts_net_join(network));
					joined = true;
				}

				var address = new byte[28];
				while (true)
				{
					timeout.Token.ThrowIfCancellationRequested();
					lock (NativeGate)
					{
						int status = networkStatus = Native.zts_net_get_status(network);
						if (status == 2)
							throw new IOException($"Not authorized for this ZeroTier network.\nAuthorize node {NodeId:x10}, then retry.");
						if (status == 3)
							throw new IOException("ZeroTier network was not found.");
						if (status is 4 or 5)
							throw new IOException("ZeroTier could not join this network.");
						if (status == 1 && Native.zts_net_transport_is_ready(network) == 1 && Native.zts_addr_get(network, 2, address) == 0 && address[1] == 2)
						{
							if (Native.zts_net_get_broadcast(network) != 1)
								throw new IOException("Enable broadcast on the ZeroTier network.");
							uint ip = BinaryPrimitives.ReadUInt32BigEndian(address.AsSpan(4, 4));
							int prefix = BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(2, 2));
							if (prefix is < 1 or > 30)
								throw new IOException("ZeroTier needs an IPv4 subnet with broadcast.");
							localAddress = ip;
							subnetMask = uint.MaxValue << (32 - prefix);
							subnet = ip & subnetMask;
							broadcast = ip | (uint.MaxValue >> prefix);
							Address = FormatAddress(ip);
							break;
						}
					}

					await Task.Delay(100, timeout.Token).ConfigureAwait(false);
				}

				timeout.Token.ThrowIfCancellationRequested();
				udp = CreateSocket(2);
				var listener = CreateSocket(1);
				udp.Call(fd => Native.zts_bind(fd, "0.0.0.0", Port)).EnsureSuccess();
				udp.Call(fd =>
				{
					int enabled = 1;
					return Native.zts_bsd_setsockopt(fd, 0xfff, 0x20, ref enabled, 4);
				}).EnsureSuccess();
				listener.Call(fd => Native.zts_bind(fd, "0.0.0.0", Port)).EnsureSuccess();
				listener.Call(fd => Native.zts_bsd_listen(fd, 32)).EnsureSuccess();
				timeout.Token.ThrowIfCancellationRequested();
				lock (NativeGate)
					ready = true;
				receivers = [ReceiveDatagramsAsync(udp), AcceptStreamsAsync(listener)];
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stop.IsCancellationRequested)
			{
				Cleanup();
				throw new TimeoutException(!online
					? "ZeroTier connection timed out.\nCheck your internet connection."
					: networkStatus == 1
						? "ZeroTier connected, but IPv4 networking did not become ready.\nCheck the network's IPv4 settings."
						: "Timed out waiting for the ZeroTier network.\nNo authorization response was received.");
			}
			catch
			{
				Cleanup();
				throw;
			}
		}
		finally
		{
			lifecycle.Release();
		}
	}

	public async ValueTask SendDatagramAsync(uint destinationIpv4, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		if (data.Length > 65507)
			throw new ArgumentException("Datagram is too large.", nameof(data));
		NativeSocket socket;
		lock (NativeGate)
		{
			EnsureReady();
			socket = udp!;
			if (destinationIpv4 == uint.MaxValue)
				destinationIpv4 = broadcast;
			else if (!IsNetworkPeer(destinationIpv4))
				throw new IOException("Address is outside the ZeroTier network.");
		}

		byte[] destination = Endpoint(destinationIpv4);
		byte[] buffer = data.IsEmpty ? new byte[1] : data.ToArray();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
		while (true)
		{
			cancellation.Token.ThrowIfCancellationRequested();
			var result = socket.Call(fd => Native.zts_bsd_sendto(fd, buffer, (nuint)data.Length, 0, destination, 16));
			if (result.Value >= 0)
			{
				if (result.Value != data.Length)
					throw new IOException("ZeroTier could not send the complete datagram.");
				return;
			}

			if (!result.Retry)
				result.EnsureSuccess();
			await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
		}
	}

	public async Task<Stream> ConnectAsync(uint destinationIpv4, CancellationToken cancellationToken)
	{
		lock (NativeGate)
		{
			EnsureReady();
			if (!IsNetworkPeer(destinationIpv4))
				throw new IOException("Address is outside the ZeroTier network.");
		}

		var socket = CreateSocket(1);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
		cancellation.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			byte[] destination = Endpoint(destinationIpv4);
			var result = socket.Call(fd => Native.zts_bsd_connect(fd, destination, 16));
			if (!result.Connected && !result.Connecting)
				result.EnsureSuccess();
			while (!result.Connected)
			{
				cancellation.Token.ThrowIfCancellationRequested();
				bool writable = false;
				socket.Call(fd =>
				{
					var poll = new PollFd
					{
						Fd = fd,
						Events = 2
					};
					int count = Native.zts_bsd_poll(ref poll, 1, 0);
					if (count > 0 && poll.Returned != 0)
					{
						int error = Native.zts_get_last_socket_error(fd);
						if (error != 0)
							throw new IOException($"ZeroTier connection failed ({error}).");
						writable = true;
					}

					return count;
				}).EnsureSuccess();
				if (writable)
					break;
				await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
			}

			cancellation.Token.ThrowIfCancellationRequested();
			return new OverlayStream(socket, stop.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stop.IsCancellationRequested)
		{
			socket.Dispose();
			throw new TimeoutException("ZeroTier connection timed out.");
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	private async Task ReceiveDatagramsAsync(NativeSocket socket)
	{
		byte[] buffer = new byte[65536], sender = new byte[16];
		try
		{
			while (!stop.IsCancellationRequested)
			{
				var result = socket.Call(fd =>
				{
					uint length = 16;
					return Native.zts_bsd_recvfrom(fd, buffer, (nuint)buffer.Length, 0, sender, ref length);
				});
				if (result.Value >= 0)
				{
					if (sender[1] != 2 || BinaryPrimitives.ReadUInt16BigEndian(sender.AsSpan(2, 2)) != Port)
						continue;
					uint ip = BinaryPrimitives.ReadUInt32BigEndian(sender.AsSpan(4, 4));
					if (!IsNetworkPeer(ip))
						continue;
					DatagramReceived?.Invoke(ip, buffer.AsSpan(0, result.Value).ToArray());
				}
				else
				{
					if (!result.Retry)
						result.EnsureSuccess();
					await Task.Delay(10, stop.Token).ConfigureAwait(false);
				}
			}
		}
		catch (Exception) when (stop.IsCancellationRequested)
		{
		}
		catch (Exception error)
		{
			ReportFault(error);
		}
	}

	private async Task AcceptStreamsAsync(NativeSocket listener)
	{
		byte[] sender = new byte[16];
		try
		{
			while (!stop.IsCancellationRequested)
			{
				NativeSocket? accepted = null;
				var result = listener.Call(fd =>
				{
					uint length = 16;
					int peer = Native.zts_bsd_accept(fd, sender, ref length);
					if (peer >= 0)
						accepted = TrackSocket(peer, 1);
					return peer;
				});
				if (accepted is not null)
				{
					uint ip = BinaryPrimitives.ReadUInt32BigEndian(sender.AsSpan(4, 4));
					if (sender[1] != 2 || !IsNetworkPeer(ip))
					{
						accepted.Dispose();
						continue;
					}

					var stream = new OverlayStream(accepted, stop.Token);
					try
					{
						if (StreamAccepted is { } handler)
							handler(ip, stream);
						else
							stream.Dispose();
					}
					catch
					{
						stream.Dispose();
						throw;
					}
				}
				else
				{
					if (!result.Retry)
						result.EnsureSuccess();
					await Task.Delay(10, stop.Token).ConfigureAwait(false);
				}
			}
		}
		catch (Exception) when (stop.IsCancellationRequested)
		{
		}
		catch (Exception error)
		{
			ReportFault(error);
		}
	}

	private void ReportFault(Exception error)
	{
		lock (NativeGate)
			ready = false;
		stop.Cancel();
		try
		{
			Faulted?.Invoke(error);
		}
		catch
		{
		}
	}

	private NativeSocket CreateSocket(int type)
	{
		lock (NativeGate)
		{
			ObjectDisposedException.ThrowIf(disposed || stop.IsCancellationRequested, this);
			int fd = Native.zts_bsd_socket(2, type, 0);
			Check(fd);
			return TrackSocket(fd, type);
		}
	}

	private NativeSocket TrackSocket(int fd, int type)
	{
		try
		{
			Check(Native.zts_set_blocking(fd, 0));
			if (type == 1)
				Check(Native.zts_set_no_delay(fd, 1));
			var socket = new NativeSocket(this, fd);
			sockets.Add(socket);
			return socket;
		}
		catch
		{
			Native.zts_bsd_close(fd);
			throw;
		}
	}

	private void EnsureReady()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (!ready)
			throw new IOException("ZeroTier is not connected.");
	}

	public ValueTask DisposeAsync()
	{
		lock (disposeGate)
			return new ValueTask(disposeTask ??= DisposeCoreAsync());
	}

	private async Task DisposeCoreAsync()
	{
		stop.Cancel();
		await lifecycle.WaitAsync().ConfigureAwait(false);
		try
		{
			lock (NativeGate)
			{
				disposed = true;
				ready = false;
				foreach (var socket in sockets.ToArray())
					socket.Dispose();
			}

			await Task.WhenAll(receivers).ConfigureAwait(false);
			Cleanup();
		}
		finally
		{
			lifecycle.Release();
		}
	}

	private void Cleanup()
	{
		lock (NativeGate)
		{
			ready = false;
			foreach (var socket in sockets.ToArray())
				socket.Dispose();
			if (joined)
			{
				Native.zts_net_leave(network);
				joined = false;
			}

			Address = "";
			if (leased)
			{
				leased = false;
				NodeLease.Release();
			}
		}
	}

	private static string FormatAddress(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

	private bool IsNetworkPeer(uint ip) => subnetMask != 0 && (ip & subnetMask) == subnet && ip != subnet && ip != broadcast && ip != localAddress && ip >> 24 is not (0 or 127) and < 224;

	private static byte[] Endpoint(uint ip)
	{
		var address = new byte[16];
		address[0] = 16;
		address[1] = 2;
		BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2, 2), Port);
		BinaryPrimitives.WriteUInt32BigEndian(address.AsSpan(4, 4), ip);
		return address;
	}

	private static void Check(int value)
	{
		if (value < 0)
			throw new IOException($"ZeroTier operation failed ({value}).");
	}

	private readonly record struct Result(int Value, int Error)
	{
		private enum WindowsErrno
		{
			Interrupted = 4,
			TryAgain = 11,
			AlreadyInProgress = 103,
			InProgress = 112,
			AlreadyConnected = 113,
			WouldBlock = 140
		}

		public bool Retry => Value == -1 && (WindowsErrno)Error is WindowsErrno.Interrupted or WindowsErrno.TryAgain or WindowsErrno.WouldBlock;

		public bool Connecting => Value == -1 && (WindowsErrno)Error is WindowsErrno.AlreadyInProgress or WindowsErrno.InProgress;

		public bool Connected => Value >= 0 || (Value == -1 && (WindowsErrno)Error == WindowsErrno.AlreadyConnected);

		public void EnsureSuccess()
		{
			if (Value < 0)
				throw new IOException($"ZeroTier socket failed ({(Value == -1 ? Error : Value)}).");
		}
	}

	private sealed class NativeSocket(ZeroTierTransport owner, int descriptor) : IDisposable
	{
		private int fd = descriptor;

		public Result Call(Func<int, int> operation)
		{
			lock (NativeGate)
			{
				ObjectDisposedException.ThrowIf(fd < 0, this);
				int value = operation(fd);
				return new Result(value, value == -1 ? Native.CSharp_zts_errno_get() : 0);
			}
		}

		public void Dispose()
		{
			lock (NativeGate)
			{
				if (fd < 0)
					return;
				Native.zts_bsd_close(fd);
				fd = -1;
				owner.sockets.Remove(this);
			}
		}
	}

	private sealed class OverlayStream(NativeSocket socket, CancellationToken lifetime) : Stream, IOverlayHalfClose
	{
		private const int BufferSize = 65536;
		private readonly SemaphoreSlim readGate = new(1, 1), writeGate = new(1, 1);
		private bool closed;

		public override bool CanRead => !closed;

		public override bool CanWrite => !closed;

		public override bool CanSeek => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException(); set => throw new NotSupportedException();
		}

		public override void Flush()
		{
			ObjectDisposedException.ThrowIf(closed, this);
		}

		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Flush();
			return Task.CompletedTask;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public void ShutdownWrite() => socket.Call(fd => Native.zts_bsd_shutdown(fd, 1)).EnsureSuccess();

		public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(closed, this);
			using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
			await readGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
			byte[]? bytes = null;
			try
			{
				if (buffer.IsEmpty)
					return 0;
				int length = Math.Min(buffer.Length, BufferSize);
				bytes = ArrayPool<byte>.Shared.Rent(length);
				while (true)
				{
					cancellation.Token.ThrowIfCancellationRequested();
					var result = socket.Call(fd => Native.zts_bsd_recv(fd, bytes, (nuint)length, 0));
					if (result.Value >= 0)
					{
						bytes.AsMemory(0, result.Value).CopyTo(buffer);
						return result.Value;
					}

					if (!result.Retry)
						result.EnsureSuccess();
					await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
				}
			}
			finally
			{
				if (bytes is not null)
					ArrayPool<byte>.Shared.Return(bytes);
				readGate.Release();
			}
		}

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(closed, this);
			using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
			await writeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
			byte[]? bytes = null;
			try
			{
				if (buffer.IsEmpty)
					return;
				bytes = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, BufferSize));
				while (!buffer.IsEmpty)
				{
					int length = Math.Min(buffer.Length, BufferSize);
					buffer[..length].CopyTo(bytes);
					while (true)
					{
						cancellation.Token.ThrowIfCancellationRequested();
						var result = socket.Call(fd => Native.zts_bsd_send(fd, bytes, (nuint)length, 0));
						if (result.Value > 0)
						{
							buffer = buffer[result.Value..];
							break;
						}

						if (result.Value == 0)
							throw new IOException("ZeroTier stream was closed.");
						if (!result.Retry)
							result.EnsureSuccess();
						await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
					}
				}
			}
			finally
			{
				if (bytes is not null)
					ArrayPool<byte>.Shared.Return(bytes);
				writeGate.Release();
			}
		}

		protected override void Dispose(bool disposing)
		{
			closed = true;
			socket.Dispose();
			base.Dispose(disposing);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollFd
	{
		public int Fd;
		public short Events, Returned;
	}

	private static class Native
	{
		private const string Library = "libzt";

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
		internal static extern int zts_init_from_storage(string path);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_id_new([Out] byte[] identity, ref uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_id_pair_is_valid([In] byte[] identity, uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_init_from_memory([In] byte[] identity, uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_init_allow_id_cache(uint allowed);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_init_allow_peer_cache(uint allowed);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_init_allow_roots_cache(uint allowed);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_init_allow_net_cache(uint allowed);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_node_start();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_node_stop();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_node_is_online();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern ulong zts_node_get_id();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_net_join(ulong network);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_net_leave(ulong network);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_net_get_status(ulong network);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_net_get_broadcast(ulong network);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_net_transport_is_ready(ulong network);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_addr_get(ulong network, uint family, [Out] byte[] address);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_socket(int family, int type, int protocol);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_close(int fd);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_shutdown(int fd, int how);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_set_blocking(int fd, int enabled);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_set_no_delay(int fd, int enabled);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bind(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string address, ushort port);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_connect(int fd, [In] byte[] address, uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_listen(int fd, int backlog);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_accept(int fd, [Out] byte[] address, ref uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_setsockopt(int fd, int level, int name, ref int value, uint length);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_poll(ref PollFd fds, uint count, int timeout);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_get_last_socket_error(int fd);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_recvfrom(int fd, [Out] byte[] buffer, nuint length, int flags, [Out] byte[] address, ref uint addressLength);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_sendto(int fd, [In] byte[] buffer, nuint length, int flags, [In] byte[] address, uint addressLength);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_recv(int fd, [Out] byte[] buffer, nuint length, int flags);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int zts_bsd_send(int fd, [In] byte[] buffer, nuint length, int flags);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int CSharp_zts_errno_get();
	}
}
