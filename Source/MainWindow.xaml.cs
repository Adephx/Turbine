using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Turbine;

public partial class MainWindow : Window
{
	private Library? library;
	private readonly ObservableCollection<Game> games = [];
	private bool busy;
	private bool exiting;
	private TrayIcon? tray;
	private readonly Networking.NetworkSession networkSession = new();
	private readonly CancellationTokenSource lifetimeCancellation = new();
	private readonly SemaphoreSlim connectionGate = new(1, 1);
	private bool networkOperationActive;
	private Task? networkRecoveryTask;
	private bool networkFailureReported;
	private Exception? deferredNetworkError;
	private Task? pendingNetworkTask;
	private string? pendingNetworkId;
	private CancellationTokenSource? pendingNetworkCancellation;
	private bool shutdownCompleted;
	private SettingsDialog? settingsDialog;
	private WindowState restoredState = WindowState.Normal;
	private CancellationTokenSource? operationCancellation;
	private string displayedAddress = "";

	private Game? Selected => GamesList.SelectedItem as Game;

	public MainWindow()
	{
		InitializeComponent();
		GamesList.ItemsSource = games;
		CollectionViewSource.GetDefaultView(games).Filter = item =>
		{
			var game = (Game)item;
			var search = SearchBox.Text.Trim();
			return game.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (game.AppId != 0 && game.AppId.ToString().Contains(search, StringComparison.Ordinal));
		};
		Loaded += OnLoaded;
		NetworkChange.NetworkAddressChanged += NetworkChanged;
		NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
		networkSession.Faulted += error => _ = Dispatcher.InvokeAsync(async () =>
		{
			UpdateAddressLabel();
			if (!exiting && !networkOperationActive && !networkFailureReported && !networkSession.Connected && library?.Preferences.UseZeroTier == true)
			{
				if (settingsDialog != null)
					deferredNetworkError = error;
				else
					await HandleNetworkFailure(error);
			}
		});
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		if (Application.Current is App { StartInTray: true })
			Hide();
		await Run(OpenLibrary);
		if (exiting)
			return;
		if (library != null)
		{
			tray = new TrayIcon(RestoreWindow, ExitFromTray, () => games, TrayAvailable, CreateGameMenu, OpenSettings, ToggleTransport, () => library.Preferences.UseZeroTier, CanChangeTransport, PlayGame);
			tray.Visible = true;
		}

		if (tray?.Visible != true)
			Close();
		else
			await SyncNetwork();
	}

	private async Task OpenLibrary()
	{
		CancelButton.Visibility = Visibility.Visible;
		await LoadLibrary();
		var cancel = operationCancellation!.Token;
		var runtime = new RuntimeDependencies(AppContext.BaseDirectory);
		var updater = new DependencyUpdater(AppContext.BaseDirectory);
		var (runtimeMissing, missing) = await Task.Run(async () => (await runtime.NeedsInstall(cancel), updater.MissingComponents()), cancel);
		if (!runtimeMissing && missing.Count == 0)
			return;
		if (!IsVisible)
			RestoreWindow();
		if (!NoticeDialog.Show(this, "Missing dependencies!\nDownload them now?", "Download"))
			return;
		var download = new OperationDialog("Downloading dependencies…", operationCancellation.Cancel)
		{
			Owner = this
		};
		await download.RunWhenNeeded(async starting =>
		{
			if (runtimeMissing)
				await runtime.Install(cancel, starting);
			if (missing.Count > 0)
				await updater.Update(cancel, confirmExclusion: component => ConfirmDownloadExclusion(component, download), downloadStarting: starting, components: missing);
		});
	}

	private async Task LoadLibrary()
	{
		library = new Library(AppContext.BaseDirectory);
		Themes.Apply(library.Preferences.Theme);
		UpdateTransportControl();
		var cancel = operationCancellation!.Token;
		var loaded = await Task.Run(() => library.Load(cancel), cancel);
		foreach (var game in loaded)
			games.Add(game);
		Filter();
		await Dispatcher.Yield(DispatcherPriority.Background);
		try
		{
			await Task.Run(() =>
			{
				foreach (var game in loaded)
					library.UpdateStatus(game, cancel);
			}, cancel);
		}
		finally
		{
			GamesList.Items.Refresh();
		}
	}

	private Task<bool> ConfirmDownloadExclusion(string component, Window owner) => Dispatcher.InvokeAsync(() => NoticeDialog.Show(
		owner.IsVisible ? owner : this,
		component + " was blocked.\nTemporarily exclude its download folder\nfrom Microsoft Defender?\n\nAdministrator approval required.",
		"Allow once"
	)).Task;

	private async Task Run(Func<Task> action)
	{
		if (busy || exiting)
			return;
		busy = true;
		operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
		LibraryPanel.IsEnabled = LibraryToolbar.IsEnabled = false;
		try
		{
			await action();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (!exiting)
			{
				if (!IsVisible)
					RestoreWindow();
				NoticeDialog.Show(this, ex.Message);
			}
		}
		finally
		{
			busy = false;
			LibraryPanel.IsEnabled = LibraryToolbar.IsEnabled = true;
			CancelButton.Visibility = Visibility.Collapsed;
			operationCancellation?.Dispose();
			operationCancellation = null;
		}
	}

	private async Task<List<string>> Refresh(IProgress<string> progress)
	{
		var errors = new List<string>();
		if (library == null)
			return errors;
		var select = Selected?.Folder;
		var cancel = operationCancellation!.Token;
		var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			var discoveryUnavailable = false;
			var scanProgress = new Progress<string>(folder => progress.Report("../" + folder));
			await library.Scan(scanProgress, cancel, async game =>
			{
				found.Add(game.Folder);
				PutGame(game);
				Filter(select, refresh: false);
				cancel.ThrowIfCancellationRequested();
				try
				{
					if (!discoveryUnavailable && game.NeedsSteamLookup)
					{
						var result = await DiscoverGame(game);
						Filter(select);
						discoveryUnavailable = result.Message.StartsWith("Steam discovery unavailable");
						if (discoveryUnavailable)
							errors.Add(result.Message);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					errors.Add(game.Name + ": " + ex.Message.Split('\n')[0]);
				}
			});
			foreach (var missing in games.Where(game => !found.Contains(game.Folder)).ToArray())
				games.Remove(missing);
		}
		finally
		{
			Filter(select, refresh: false);
		}

		return errors;
	}

	private void PutGame(Game game)
	{
		for (var i = 0; i < games.Count; i++)
		{
			var order = StringComparer.OrdinalIgnoreCase.Compare(games[i].Folder, game.Folder);
			if (order == 0)
			{
				games[i] = game;
				return;
			}

			if (order > 0)
			{
				games.Insert(i, game);
				return;
			}
		}

		games.Add(game);
	}

	private void Filter(string? select = null, bool refresh = true)
	{
		if (GamesList == null)
			return;
		if (refresh)
			CollectionViewSource.GetDefaultView(games).Refresh();
		GamesList.SelectedItem = GamesList.Items.Cast<Game>().FirstOrDefault(g => g.Folder.Equals(select, StringComparison.OrdinalIgnoreCase));
		EmptyLibrary.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		var count = GamesList.Items.Count;
		CountLabel.Text = $"{count} Game{(count == 1 ? "" : "s")}";
	}

	private void SearchChanged(object sender, TextChangedEventArgs e) => Filter(Selected?.Folder);

	private void SelectRightClickedGame(object sender, MouseButtonEventArgs e)
	{
		if (ItemsControl.ContainerFromElement(GamesList, e.OriginalSource as DependencyObject) is ListBoxItem item)
			item.IsSelected = true;
	}

	private void ShowSettings(object sender, RoutedEventArgs e) => OpenSettings();

	private async void OpenSettings()
	{
		if (!TrayAvailable() || library == null)
			return;
		RestoreWindow();
		settingsDialog = new SettingsDialog(library, games, SavePreferencesAsync, NetworkConnected, () => networkSession.HasRunningGames, lifetimeCancellation.Token)
		{
			Owner = this
		};
		try
		{
			settingsDialog.ShowDialog();
		}
		finally
		{
			settingsDialog = null;
		}

		UpdateTransportControl();
		var error = deferredNetworkError;
		deferredNetworkError = null;
		if (error != null && !exiting && library.Preferences.UseZeroTier && !NetworkConnected(library.Preferences.ZeroTierNetwork))
			await HandleNetworkFailure(error);
	}

	private bool NetworkConnected(string id) => networkSession.Connected && string.Equals(networkSession.NetworkId, id, StringComparison.OrdinalIgnoreCase);

	private void UpdateTransportControl()
	{
		var useZeroTier = library?.Preferences.UseZeroTier == true;
		TransportToggle.IsChecked = useZeroTier;
		TransportToggle.IsEnabled = useZeroTier || Preferences.IsValidNetworkId(library?.Preferences.ZeroTierNetwork);
		TransportToggle.Content = useZeroTier ? "ON" : "OFF";
		AutomationProperties.SetName(TransportToggle, useZeroTier ? "V-LAN: On" : "V-LAN: Off");
		UpdateAddressLabel();
	}

	private void UpdateAddressLabel()
	{
		if (library == null)
			return;
		var preferences = library.Preferences;
		var connectingTo = pendingNetworkCancellation != null ? pendingNetworkId : null;
		var networkId = connectingTo ?? (preferences.UseZeroTier ? preferences.ZeroTierNetwork : null);
		var placeholder = "Not connected";
		string address;
		if (networkId == null)
			address = Networking.LanAddress.GetPrimaryIPv4()?.ToString() ?? "";
		else
		{
			address = NetworkConnected(networkId) ? networkSession.Address : "";
			if (pendingNetworkTask == null || networkOperationActive)
				placeholder = "Obtaining…";
		}
		if (address != displayedAddress)
		{
			CopyStatus.BeginAnimation(OpacityProperty, null);
			CopyStatus.Opacity = 0;
		}
		displayedAddress = address;
		AddressButton.Content = address.Length == 0 ? placeholder : address;
		AddressButton.IsEnabled = displayedAddress.Length != 0;
	}

	private void NetworkChanged(object? sender, EventArgs e)
	{
		if (!Dispatcher.HasShutdownStarted)
			_ = Dispatcher.InvokeAsync(() =>
			{
				if (!exiting)
					UpdateAddressLabel();
			});
	}

	private void CopyAddressClick(object sender, RoutedEventArgs e)
	{
		if (displayedAddress.Length == 0)
			return;
		try
		{
			Clipboard.SetText(displayedAddress);
			CopyStatus.BeginAnimation(OpacityProperty, null);
			CopyStatus.Opacity = 1;
			CopyStatus.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
			{
				BeginTime = TimeSpan.FromSeconds(2)
			});
		}
		catch (COMException)
		{
			NoticeDialog.Show(this, "Could not copy the IP address.");
		}
	}

	private bool CanChangeTransport() => library != null && !busy && !exiting && !networkOperationActive && settingsDialog == null && !networkSession.HasRunningGames
		&& (library.Preferences.UseZeroTier || Preferences.IsValidNetworkId(library.Preferences.ZeroTierNetwork));

	private async void ToggleTransport() => await ChangeTransport(library?.Preferences.UseZeroTier != true, quiet: true);

	private async void TransportClick(object sender, RoutedEventArgs e) => await ChangeTransport(TransportToggle.IsChecked == true);

	private async Task ChangeTransport(bool useZeroTier, bool quiet = false)
	{
		if (busy || library == null || exiting || networkOperationActive || settingsDialog != null)
		{
			UpdateTransportControl();
			return;
		}

		UpdateTransportControl();
		if (networkSession.HasRunningGames)
		{
			RestoreWindow();
			NoticeDialog.Show(this, "Close running games before changing the network.");
			return;
		}

		if (useZeroTier && !Preferences.IsValidNetworkId(library.Preferences.ZeroTierNetwork))
		{
			RestoreWindow();
			NoticeDialog.Show(this, "Set the ZeroTier network ID in Settings first.");
			return;
		}

		await Run(async () =>
		{
			var next = library.Preferences with
			{
				UseZeroTier = useZeroTier
			};
			if (useZeroTier && !quiet)
			{
				var dialog = new OperationDialog("Connecting…", operationCancellation!.Cancel)
				{
					Owner = this
				};
				await dialog.Run(_ => SavePreferencesAsync(next, operationCancellation.Token));
			}
			else
				await SavePreferencesAsync(next, operationCancellation!.Token);
		});
	}

	private async Task SavePreferencesAsync(Preferences preferences, CancellationToken cancel)
	{
		if (library is null)
			return;
		preferences.Validate();
		var previous = library.Preferences;
		var networkChanged = preferences.UseZeroTier != previous.UseZeroTier || !string.Equals(preferences.ZeroTierNetwork, previous.ZeroTierNetwork, StringComparison.OrdinalIgnoreCase);
		if (networkChanged && networkSession.HasRunningGames)
			throw new InvalidOperationException("Close running games before changing the network.");
		try
		{
			if (networkChanged)
				await SetNetwork(preferences.UseZeroTier ? preferences.ZeroTierNetwork : null, cancel);
			cancel.ThrowIfCancellationRequested();
			library.SavePreferences(preferences);
		}
		catch (Exception error)
		{
			if (networkChanged && !lifetimeCancellation.IsCancellationRequested)
			{
				using var restore = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
				restore.CancelAfter(TimeSpan.FromSeconds(10));
				try
				{
					await SetNetwork(previous.UseZeroTier ? previous.ZeroTierNetwork : null, restore.Token);
				}
				catch (Exception)
				{
					await SetNetwork(null, lifetimeCancellation.Token);
					var message = error.Message + "\nCould not restore V-LAN. Switched to LAN.";
					try
					{
						library.SavePreferences(previous with
						{
							UseZeroTier = false
						});
					}
					catch (Exception saveError)
					{
						library.Preferences.UseZeroTier = false;
						throw new IOException(message + "\nCould not save the network mode.", new AggregateException(error, saveError));
					}

					throw new IOException(message, error);
				}
			}

			throw;
		}
		finally
		{
			UpdateTransportControl();
		}
	}

	private Task SetNetwork(string? networkId, CancellationToken cancellationToken)
	{
		if (pendingNetworkTask is { IsCompleted: false } && string.Equals(pendingNetworkId, networkId, StringComparison.OrdinalIgnoreCase))
			return pendingNetworkTask.WaitAsync(cancellationToken);
		pendingNetworkCancellation?.Cancel();
		var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
		pendingNetworkCancellation = cancellation;
		pendingNetworkId = networkId;
		pendingNetworkTask = ChangeNetwork(networkId, cancellation);
		return pendingNetworkTask;
	}

	private async Task ChangeNetwork(string? networkId, CancellationTokenSource cancellation)
	{
		try
		{
			await SetNetworkCore(networkId, cancellation.Token);
		}
		finally
		{
			if (ReferenceEquals(pendingNetworkCancellation, cancellation))
				pendingNetworkCancellation = null;
			cancellation.Dispose();
		}
	}

	private async Task SetNetworkCore(string? networkId, CancellationToken cancellationToken)
	{
		if (library == null)
			return;
		await connectionGate.WaitAsync(cancellationToken);
		networkOperationActive = true;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (networkId != null && NetworkConnected(networkId))
				return;
			if (networkId == null && !networkSession.Connected && string.IsNullOrEmpty(networkSession.NetworkId))
				return;
			if (networkSession.HasRunningGames)
				throw new InvalidOperationException("Close running games before changing the network.");
			await networkSession.DisconnectAsync();
			UpdateAddressLabel();
			if (networkId != null)
			{
				var identity = await library.GetNetworkIdentityAsync(cancellationToken);
				await networkSession.ConnectAsync(networkId, identity, cancellationToken);
				networkFailureReported = false;
				deferredNetworkError = null;
			}
		}
		finally
		{
			networkOperationActive = false;
			connectionGate.Release();
			UpdateAddressLabel();
		}
	}

	private Task ApplySavedNetwork()
	{
		var preferences = library!.Preferences;
		return SetNetwork(preferences.UseZeroTier ? preferences.ZeroTierNetwork : null, lifetimeCancellation.Token);
	}

	private async Task SyncNetwork()
	{
		if (library == null || exiting)
			return;
		try
		{
			await ApplySavedNetwork();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (settingsDialog == null)
				await HandleNetworkFailure(ex);
			else
				deferredNetworkError = ex;
		}
	}

	private Task HandleNetworkFailure(Exception error)
	{
		if (exiting || library == null)
			return Task.CompletedTask;
		if (networkRecoveryTask is { IsCompleted: false })
			return networkRecoveryTask;
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		networkRecoveryTask = completion.Task;
		_ = RecoverNetwork(error, completion);
		return networkRecoveryTask;
	}

	private async Task RecoverNetwork(Exception error, TaskCompletionSource completion)
	{
		networkFailureReported = true;
		try
		{
			while (!exiting)
			{
				if (!IsVisible)
					RestoreWindow();
				if (networkSession.HasRunningGames)
				{
					NoticeDialog.Show(this, "ZeroTier disconnected.\nClose running games to reconnect.");
					return;
				}

				if (!NoticeDialog.Show(this, error.Message, "Retry", "Use LAN"))
				{
					await SavePreferencesAsync(library!.Preferences with
					{
						UseZeroTier = false
					}, lifetimeCancellation.Token);
					return;
				}

				try
				{
					using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
					var dialog = new OperationDialog("Connecting…", retryCancellation.Cancel)
					{
						Owner = this
					};
					await dialog.Run(_ => SetNetwork(library!.Preferences.ZeroTierNetwork, retryCancellation.Token));
					return;
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					error = ex;
				}
			}
		}
		catch (Exception ex)
		{
			if (!exiting)
				NoticeDialog.Show(this, ex.Message);
		}
		finally
		{
			completion.TrySetResult();
		}
	}

	private async void RefreshClick(object sender, RoutedEventArgs e)
	{
		if (busy || library == null)
			return;
		await Run(async () =>
		{
			List<string> errors = [];
			var dialog = new OperationDialog("../Games", operationCancellation!.Cancel)
			{
				Owner = this
			};
			await dialog.Run(async progress =>
			{
				errors = await Refresh(progress);
				try
				{
					await new DependencyUpdater(AppContext.BaseDirectory).Update(operationCancellation.Token,
						confirmExclusion: component => ConfirmDownloadExclusion(component, dialog));
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (IOException ex)
				{
					errors.Add(ex.Message);
				}
			});
			if (errors.Count > 0)
				NoticeDialog.Show(this, string.Join("\n\n", errors));
		});
	}

	private Task ImportGame(string source) => Run(async () =>
	{
		CancelButton.Visibility = Visibility.Visible;
		var game = await library!.Import(source, operationCancellation!.Token);
		PutGame(game);
		Filter(game.Folder, refresh: false);
		if (game.NeedsSteamLookup)
			await DiscoverGame(game);
		var previousId = game.AppId;
		if ((!game.IsNonSteam && game.AppId == 0) || string.IsNullOrWhiteSpace(game.Executable))
			EditGame(game);
		if (game.NeedsSteamLookup && previousId != game.AppId)
			await DiscoverGame(game);
		Filter(game.Folder);
	});

	private async void OnDrop(object sender, DragEventArgs e)
	{
		if (busy || library == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
			return;
		var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
		if (paths.Length == 1 && Directory.Exists(paths[0]))
			await ImportGame(paths[0]);
		else
			NoticeDialog.Show(this, "Drop one game folder at a time.");
	}

	private void CancelClick(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

	private async Task<DiscoveryResult> DiscoverGame(Game game)
	{
		var result = await library!.Discovery.Discover(library, game, operationCancellation!.Token);
		game.SteamMatches = result.Matches ?? [];
		return result;
	}

	private bool EditGame(Game game)
	{
		var dialog = new GameDialog(library!, game)
		{
			Owner = this
		};
		dialog.ShowDialog();
		return dialog.Saved;
	}

	private Game? ContextGame(object sender) => (sender as FrameworkElement)?.DataContext as Game ?? Selected;

	private void ConfigureClick(object sender, RoutedEventArgs e)
	{
		if (busy || ContextGame(sender) is not { } game || library == null)
			return;
		try
		{
			library.EnsureNotRunning(game);
			RestoreWindow();
			if (EditGame(game))
				Filter(game.Folder);
		}
		catch (Exception ex)
		{
			RestoreWindow();
			NoticeDialog.Show(this, ex.Message);
		}
	}

	private async void PatchClick(object sender, RoutedEventArgs e)
	{
		if (busy || ContextGame(sender) is not { } game || game.IsNonSteam || library == null)
			return;
		RestoreWindow();
		await Run(async () =>
		{
			var dialog = new OperationDialog("../" + game.Folder)
			{
				Owner = this
			};
			try
			{
				await dialog.Run(_ => Task.Run(() => library.Emulator.Apply(game)));
			}
			finally
			{
				GamesList.Items.Refresh();
			}
		});
	}

	private async void PlayClick(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (ContextGame(sender) is { } game)
			await PlayGame(game);
	}

	private async Task PlayGame(Game game)
	{
		if (library == null)
			return;
		await Run(async () =>
		{
			if (library.Preferences.UseZeroTier && !NetworkConnected(library.Preferences.ZeroTierNetwork))
			{
				await SyncNetwork();
				if (library.Preferences.UseZeroTier && !NetworkConnected(library.Preferences.ZeroTierNetwork))
					return;
			}

			var launch = library.LaunchInfo(game);
			await networkSession.LaunchAsync(launch, game.AppId, library.Preferences.UseZeroTier, operationCancellation!.Token);
			if (tray?.Visible == true)
				Hide();
		});
	}

	private async void GameDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(GamesList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: Game game })
			await PlayGame(game);
	}

	private void OpenFolder(string path)
	{
		Directory.CreateDirectory(path);
		Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
	}

	private async void OpenGameClick(object sender, RoutedEventArgs e)
	{
		if (ContextGame(sender) is { } game && library != null)
			await Run(() =>
			{
				OpenFolder(library.GamePath(game));
				return Task.CompletedTask;
			});
	}

	private async void OpenSettingsClick(object sender, RoutedEventArgs e)
	{
		if (ContextGame(sender) is { } game && library != null)
			await Run(() =>
			{
				OpenFolder(library.SettingsPath(game));
				return Task.CompletedTask;
			});
	}

	private async void DeleteUserSettingsClick(object sender, RoutedEventArgs e)
	{
		if (busy || exiting || ContextGame(sender) is not { } game || library == null || game.AppId == 0)
			return;
		RestoreWindow();
		if (!NoticeDialog.Show(
			this,
			$"Permanently delete user settings for {game.Name}?\n\nThis includes saved progress in this\nTurbine library for Steam App ID {game.AppId}.",
			"Delete"
		))
			return;
		await Run(() => Task.Run(() => library.DeleteUserSettings(game)));
	}

	private async void DeleteAllSettingsClick(object sender, RoutedEventArgs e)
	{
		if (busy || exiting || ContextGame(sender) is not { } game || library == null)
			return;
		RestoreWindow();
		if (!NoticeDialog.Show(
			this,
			$"Permanently delete all settings for {game.Name}?\n\nSaved progress will be lost. Game files stay.\nUse Refresh to detect the game again.",
			"Delete"
		))
			return;
		await Run(async () =>
		{
			await Task.Run(() => library.DeleteAllSettings(game));
			games.Remove(game);
			Filter();
		});
	}

	private async void DeleteGameClick(object sender, RoutedEventArgs e)
	{
		if (busy || exiting || ContextGame(sender) is not { } game || library == null)
			return;
		RestoreWindow();
		if (!NoticeDialog.Show(this, $"Permanently delete {game.Name}?\n\nThe game files will be removed.", "Delete"))
			return;
		var deleteSettings = NoticeDialog.Show(this, $"Also delete all settings for {game.Name}?\n\nThis includes saved progress in this\nTurbine library.", "Delete settings", "Keep settings");
		await Run(async () =>
		{
			await Task.Run(() => library.DeleteGame(game, deleteSettings));
			games.Remove(game);
			Filter();
		});
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (shutdownCompleted)
			return;
		if (exiting)
		{
			e.Cancel = true;
			return;
		}

		if (busy)
		{
			e.Cancel = true;
			NoticeDialog.Show(
				this,
				CancelButton.IsVisible ? "Cancel the current operation\nbefore closing." : "Wait for the current operation\nto finish."
			);
		}
		else if (tray?.Visible == true)
		{
			e.Cancel = true;
			Hide();
		}
		else
		{
			e.Cancel = true;
			ExitApplication();
		}
	}

	private ContextMenu CreateGameMenu(Game game)
	{
		var menu = (ContextMenu)FindResource("GameMenu");
		menu.DataContext = game;
		return menu;
	}

	private bool TrayAvailable() => !busy && !exiting && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

	internal void RestoreWindow()
	{
		if (exiting)
			return;
		ShowInTaskbar = true;
		ShowActivated = true;
		Show();
		if (WindowState == WindowState.Minimized)
			WindowState = restoredState;
		Activate();
		OwnedWindows.Cast<Window>().LastOrDefault(window => window.IsVisible)?.Activate();
	}

	private void ExitFromTray()
	{
		if (TrayAvailable())
			ExitApplication();
	}

	internal bool ExitApplication(bool sessionEnding = false)
	{
		if (shutdownCompleted)
			return true;
		if (busy)
			return false;
		if (exiting)
			return sessionEnding;
		exiting = true;
		lifetimeCancellation.Cancel();
		_ = Shutdown();
		return sessionEnding;
	}

	private async Task Shutdown()
	{
		await Dispatcher.Yield(DispatcherPriority.Background);
		await connectionGate.WaitAsync();
		try
		{
			await networkSession.DisposeAsync();
		}
		catch (Exception ex)
		{
			if (IsVisible)
				NoticeDialog.Show(this, ex.Message);
		}
		finally
		{
			connectionGate.Release();
			shutdownCompleted = true;
			Close();
		}
	}

	protected override void OnStateChanged(EventArgs e)
	{
		if (WindowState != WindowState.Minimized)
			restoredState = WindowState;
		base.OnStateChanged(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		NetworkChange.NetworkAddressChanged -= NetworkChanged;
		NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
		tray?.Dispose();
		tray = null;
		lifetimeCancellation.Dispose();
		base.OnClosed(e);
	}
}
