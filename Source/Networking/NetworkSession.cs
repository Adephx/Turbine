using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Turbine.Networking;

internal sealed class NetworkSession : IAsyncDisposable
{
	private readonly SemaphoreSlim transition = new(1, 1);
	private readonly object gamesGate = new();
	private readonly Dictionary<Process, NetworkBroker.GameClient?> games = [];
	private readonly Func<IOverlayTransport> createTransport;
	private Action<Exception>? faultHandler;
	private IOverlayTransport? transport;
	private NetworkBroker? broker;
	private NetworkBroker? lanBroker;
	private bool disposed;
	private volatile bool connected;

	public event Action<Exception>? Faulted;

	public bool Connected => connected;

	public bool HasRunningGames
	{
		get
		{
			Process[] tracked;
			lock (gamesGate)
				tracked = games.Keys.ToArray();
			// HasExited can invoke Exited handlers, which also acquire gamesGate.
			return tracked.Any(IsRunning) || broker?.HasRunningChildren == true || lanBroker?.HasRunningChildren == true;
		}
	}

	public string NetworkId { get; private set; } = "";

	public string Address => connected ? transport?.Address ?? "" : "";

	public NetworkSession() : this(() => new ZeroTierTransport())
	{
	}

	internal NetworkSession(Func<IOverlayTransport> createTransport) => this.createTransport = createTransport;

	public async Task ConnectAsync(string networkId, string identity, CancellationToken cancellationToken)
	{
		if (networkId.Length != 16 || !ulong.TryParse(networkId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) || id == 0)
			throw new ArgumentException("Enter a 16-digit ZeroTier network ID.", nameof(networkId));
		await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			string normalized = id.ToString("x16");
			if (connected && NetworkId == normalized)
				return;
			EnsureNoGames();
			await DisconnectCoreAsync().ConfigureAwait(false);
			var next = createTransport();
			transport = next;
			faultHandler = error => TransportFaulted(next, error);
			next.Faulted += faultHandler;
			try
			{
				await Task.Run(() => next.StartAsync(id, identity, cancellationToken), cancellationToken).ConfigureAwait(false);
				broker = new NetworkBroker(next);
				broker.Faulted += faultHandler;
				broker.ChildAuthorized += ChildAuthorized;
				NetworkId = normalized;
				connected = true;
			}
			catch
			{
				await DisconnectCoreAsync().ConfigureAwait(false);
				throw;
			}
		}
		finally
		{
			transition.Release();
		}
	}

	public async Task DisconnectAsync()
	{
		await transition.WaitAsync().ConfigureAwait(false);
		try
		{
			EnsureNoGames();
			await DisconnectCoreAsync().ConfigureAwait(false);
		}
		finally
		{
			transition.Release();
		}
	}

	public async Task<Process> LaunchAsync(ProcessStartInfo info, uint appId, bool useZeroTier, CancellationToken cancellationToken)
	{
		await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
		NetworkBroker.GameClient? client = null;
		Process? process = null;
		try
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			cancellationToken.ThrowIfCancellationRequested();
			if (useZeroTier)
			{
				if (appId == 0)
					throw new ArgumentException("A game needs a valid app ID.", nameof(appId));
				if (!connected || broker is null)
					throw new InvalidOperationException("Connect to ZeroTier before launching the game.");
				client = broker.CreateClient(appId);
			}
			else
			{
				if (lanBroker is null)
				{
					lanBroker = new NetworkBroker();
					lanBroker.ChildAuthorized += ChildAuthorized;
				}
				client = lanBroker.CreateClient(appId);
			}
			process = await NativeLaunch.StartAsync(info, client, cancellationToken).ConfigureAwait(false);
			TrackProcess(process, client);
			return process;
		}
		catch
		{
			if (process is not null)
			{
				process.Exited -= GameExited;
				lock (gamesGate)
					games.Remove(process);
				try
				{
					if (IsRunning(process))
						process.Kill(true);
				}
				catch (Exception)
				{
				}

				process.Dispose();
			}

			if (client is not null)
				await client.DisposeAsync().ConfigureAwait(false);
			throw;
		}
		finally
		{
			transition.Release();
		}
	}

	private static bool IsRunning(Process process)
	{
		try
		{
			return !process.HasExited;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private void GameExited(object? sender, EventArgs e)
	{
		if (sender is Process process)
			RemoveGame(process);
	}

	private void TrackProcess(Process process, NetworkBroker.GameClient? client)
	{
		lock (gamesGate)
			games.Add(process, client);
		process.Exited += GameExited;
		try
		{
			process.EnableRaisingEvents = true;
		}
		catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception && !IsRunning(process))
		{
			RemoveGame(process);
			return;
		}

		if (!IsRunning(process))
			RemoveGame(process);
	}

	private void ChildAuthorized(int pid)
	{
		Process? child = null;
		try
		{
			child = Process.GetProcessById(pid);
			TrackProcess(child, null);
		}
		catch (ArgumentException)
		{
			child?.Dispose();
		}
		catch
		{
			if (child is not null)
			{
				child.Exited -= GameExited;
				lock (gamesGate)
					games.Remove(child);
				child.Dispose();
			}

			throw;
		}
	}

	private void RemoveGame(Process process)
	{
		NetworkBroker.GameClient? client;
		lock (gamesGate)
		{
			if (!games.Remove(process, out client))
				return;
		}

		process.Exited -= GameExited;
		if (client is not null)
			_ = DisposeClientAsync(client);
		process.Dispose();
	}

	private async Task DisposeClientAsync(NetworkBroker.GameClient client)
	{
		try
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception error)
		{
			NotifyFault(error);
		}
	}

	private void EnsureNoGames()
	{
		if (HasRunningGames)
			throw new InvalidOperationException("Close running games before changing the network.");
	}

	private void TransportFaulted(IOverlayTransport source, Exception error) => _ = HandleTransportFaultAsync(source, error);

	private async Task HandleTransportFaultAsync(IOverlayTransport source, Exception error)
	{
		await transition.WaitAsync().ConfigureAwait(false);
		bool current = false;
		try
		{
			if (!ReferenceEquals(transport, source))
				return;
			current = true;
			await DisconnectCoreAsync().ConfigureAwait(false);
		}
		catch (Exception)
		{
		}
		finally
		{
			transition.Release();
		}

		if (current)
			NotifyFault(error);
	}

	private void NotifyFault(Exception error)
	{
		try
		{
			Faulted?.Invoke(error);
		}
		catch (Exception)
		{
		}
	}

	private async Task DisconnectCoreAsync()
	{
		connected = false;
		var currentBroker = broker;
		var currentTransport = transport;
		var currentHandler = faultHandler;
		broker = null;
		transport = null;
		faultHandler = null;
		NetworkId = "";

		if (currentTransport is not null)
			currentTransport.Faulted -= currentHandler;
		try
		{
			if (currentBroker is not null)
			{
				currentBroker.Faulted -= currentHandler;
				currentBroker.ChildAuthorized -= ChildAuthorized;
				await currentBroker.DisposeAsync().ConfigureAwait(false);
			}
		}
		finally
		{
			if (currentTransport is not null)
				await currentTransport.DisposeAsync().ConfigureAwait(false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await transition.WaitAsync().ConfigureAwait(false);
		try
		{
			if (disposed)
				return;
			disposed = true;
			if (lanBroker is not null)
			{
				lanBroker.ChildAuthorized -= ChildAuthorized;
				await lanBroker.DisposeAsync().ConfigureAwait(false);
			}
			await DisconnectCoreAsync().ConfigureAwait(false);
			Process[] tracked;
			lock (gamesGate)
			{
				tracked = games.Keys.ToArray();
				games.Clear();
			}
			foreach (var game in tracked)
			{
				game.Exited -= GameExited;
				game.Dispose();
			}
		}
		finally
		{
			transition.Release();
		}
	}
}
