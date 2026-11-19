using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Turbine;

public sealed class SettingsDialog : Window
{
	private const int ButtonWidth = 88;
	private const int ControlHeight = 32;
	private readonly Library library;
	private readonly IReadOnlyList<Game> games;
	private readonly Func<Preferences, CancellationToken, Task> savePreferences;
	private readonly Func<string, bool> networkConnected;
	private readonly Func<bool> hasRunningGames;
	private readonly CancellationTokenSource cancellation;
	private readonly StackPanel contentPanel = new();
	private readonly StackPanel actions = new();
	private bool saving;
	private bool savedStartupEnabled;
	private readonly TextBox usernameInput = new()
	{
		MaxLength = 64
	};
	private readonly TextBox steamIdInput = new()
	{
		MaxLength = 20
	};
	private readonly ComboBox languageInput = new()
	{
		ItemsSource = SteamLanguages.All,
		DisplayMemberPath = nameof(SteamLanguage.DisplayName),
		SelectedValuePath = nameof(SteamLanguage.Code),
		IsEditable = false
	};
	private readonly TextBox networkIdInput = new()
	{
		MaxLength = 16,
		ToolTip = "Embedded ZeroTier network ID."
	};
	private readonly Button saveButton = new()
	{
		Content = "Save",
		Width = ButtonWidth,
		IsDefault = true,
		Margin = new Thickness(0, 0, 8, 0)
	};
	private readonly CheckBox startupInput = new()
	{
		Content = "Start with Windows",
		Margin = new Thickness(0, 6, 0, 6),
		ToolTip = "Open Turbine in the tray when you sign in."
	};
	private readonly ComboBox themeInput = new()
	{
		SelectedValuePath = nameof(ThemeChoice.Id)
	};

	public SettingsDialog(
		Library library,
		IReadOnlyList<Game> games,
		Func<Preferences, CancellationToken, Task> savePreferences,
		Func<string, bool> networkConnected,
		Func<bool> hasRunningGames,
		CancellationToken cancellationToken
	)
	{
		this.library = library;
		this.games = games;
		this.savePreferences = savePreferences;
		this.networkConnected = networkConnected;
		this.hasRunningGames = hasRunningGames;
		cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Title = "Settings";
		WindowStyle = WindowStyle.SingleBorderWindow;
		ResizeMode = ResizeMode.CanResize;
		Style = (Style)FindResource(typeof(Window));
		UseLayoutRounding = true;
		Width = Math.Min(400, SystemParameters.WorkArea.Width - 32);
		MinWidth = Math.Min(320, Width);
		SizeToContent = SizeToContent.Height;
		MaxHeight = SystemParameters.WorkArea.Height - 32;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		usernameInput.Padding = steamIdInput.Padding = networkIdInput.Padding = new Thickness(3, 4, 6, 4);
		usernameInput.Text = library.Preferences.Username;
		steamIdInput.Text = library.Preferences.SteamId;
		languageInput.SelectedValue = SteamLanguages.Normalize(library.Preferences.Language);
		networkIdInput.Text = library.Preferences.ZeroTierNetwork;
		savedStartupEnabled = WindowsStartup.IsEnabled();
		startupInput.IsChecked = savedStartupEnabled;
		themeInput.ItemsSource = Themes.Choices;
		themeInput.SelectedValue = Themes.Normalize(library.Preferences.Theme);
		AutomationProperties.SetName(usernameInput, "Username");
		AutomationProperties.SetName(steamIdInput, "Player ID");
		AutomationProperties.SetName(languageInput, "Game language");
		AutomationProperties.SetName(networkIdInput, "ZeroTier network ID");
		AutomationProperties.SetName(themeInput, "Theme");
		themeInput.SelectionChanged += ThemeChanged;
		UpdateNetworkControls();
		var root = new Grid { Margin = new Thickness(14) };
		root.RowDefinitions.Add(new RowDefinition());
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		var footer = new Border { Child = actions, HorizontalAlignment = HorizontalAlignment.Left };
		footer.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = contentPanel });
		Grid.SetRow(footer, 1);
		actions.Margin = new Thickness(0, 12, 0, 0);
		actions.Height = ControlHeight;
		actions.Orientation = Orientation.Horizontal;
		actions.HorizontalAlignment = HorizontalAlignment.Right;
		var cancelButton = new Button
		{
			Content = "Cancel",
			Width = ButtonWidth,
			IsCancel = true
		};
		cancelButton.Click += (_, _) =>
		{
			if (!saving)
				DialogResult = false;
		};
		saveButton.Click += async (_, _) =>
		{
			if (await SaveAsync())
				DialogResult = true;
		};
		actions.Children.Add(saveButton);
		actions.Children.Add(cancelButton);
		AddField("Username", usernameInput);
		var idRow = new Grid();
		idRow.ColumnDefinitions.Add(new ColumnDefinition());
		idRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		var generate = new Button
		{
			Content = "Generate",
			Width = ButtonWidth,
			Margin = new Thickness(8, 0, 0, 0)
		};
		generate.Click += (_, _) => steamIdInput.Text = Preferences.NewId();
		Grid.SetColumn(generate, 1);
		idRow.Children.Add(steamIdInput);
		idRow.Children.Add(generate);
		AddField("Player ID", idRow);
		AddField("Language", languageInput);
		AddField("ZeroTier network ID", networkIdInput);
		AddField("Theme", themeInput);
		contentPanel.Children.Add(startupInput);
		root.Children.Add(new ScrollViewer { Content = contentPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
		root.Children.Add(footer);
		Content = root;
		Closing += OnClosing;
		Closed += (_, _) => cancellation.Dispose();
	}

	private string NetworkId => networkIdInput.Text.Trim().ToLowerInvariant();

	private void UpdateNetworkControls() => networkIdInput.IsEnabled = !saving && !hasRunningGames();

	private void AddField(string label, FrameworkElement control)
	{
		contentPanel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 6) });
		control.Height = ControlHeight;
		control.Margin = new Thickness(0, 0, 0, 12);
		contentPanel.Children.Add(control);
	}

	private void ThemeChanged(object sender, SelectionChangedEventArgs e)
	{
		if (themeInput.SelectedValue is string theme)
			Themes.Apply(theme);
	}

	private async Task<bool> SaveAsync()
	{
		if (saving)
			return false;
		var previous = library.Preferences;
		var networkChanged = !string.Equals(NetworkId, previous.ZeroTierNetwork, StringComparison.OrdinalIgnoreCase);
		var preferences = previous with
		{
			Username = usernameInput.Text.Trim(),
			SteamId = steamIdInput.Text.Trim(),
			Language = languageInput.SelectedValue as string ?? "",
			ZeroTierNetwork = NetworkId,
			UseZeroTier = networkChanged ? NetworkId.Length > 0 : previous.UseZeroTier,
			Theme = Themes.Normalize(themeInput.SelectedValue as string),
			BroadcastAddress = ""
		};
		try
		{
			if (!SteamLanguages.IsSupported(preferences.Language))
				throw new InvalidOperationException("Choose a Steam language.");
			preferences.Validate();
			if (hasRunningGames() && networkChanged)
				throw new InvalidOperationException("Close running games before changing the network.");
			var startupEnabled = startupInput.IsChecked == true;
			var startupChanged = startupEnabled != savedStartupEnabled;
			if (preferences == previous && !startupChanged)
				return true;
			saving = true;
			contentPanel.IsEnabled = actions.IsEnabled = false;
			using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
			if (preferences != previous)
			{
				if (networkChanged && preferences.UseZeroTier && !networkConnected(preferences.ZeroTierNetwork))
				{
					var dialog = new OperationDialog("Connecting…", operationCancellation.Cancel)
					{
						Owner = this
					};
					await dialog.Run(_ => savePreferences(preferences, operationCancellation.Token));
				}
				else
					await savePreferences(preferences, operationCancellation.Token);
			}
			Themes.Apply(preferences.Theme);
			var emulatorSettingsChanged = previous.Username != preferences.Username || previous.SteamId != preferences.SteamId || previous.Language != preferences.Language;
			var pending = new List<string>();
			if (emulatorSettingsChanged || previous.BroadcastAddress != preferences.BroadcastAddress)
			{
				await Task.Run(() =>
				{
					foreach (var game in games)
					{
						try
						{
							var updated = emulatorSettingsChanged ? library.Emulator.UpdateSettings(game) : library.Emulator.UpdateBroadcastSettings(game);
							if (!updated && game.IsPatched)
								pending.Add(game.Name + ": check its save-folder path in steam_settings/configs.user.ini.");
						}
						catch (Exception ex)
						{
							pending.Add(game.Name + ": " + ex.Message.Split('\n')[0]);
						}
					}
				});
			}

			if (startupChanged)
			{
				WindowsStartup.SetEnabled(startupEnabled);
				savedStartupEnabled = startupEnabled;
			}
			if (pending.Count > 0)
				NoticeDialog.Show(this, "Settings saved. Could not update:\n\n" + string.Join("\n\n", pending));

			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			NoticeDialog.Show(this, ex.Message);
			return false;
		}
		finally
		{
			saving = false;
			contentPanel.IsEnabled = actions.IsEnabled = true;
			UpdateNetworkControls();
		}
	}

	private void OnClosing(object? sender, CancelEventArgs e)
	{
		if (saving)
		{
			e.Cancel = true;
			return;
		}

		cancellation.Cancel();
		Themes.Apply(library.Preferences.Theme);
	}
}
