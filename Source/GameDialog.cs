using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Turbine;

public sealed class GameDialog : Window
{
	public bool Saved
	{
		get; private set;
	}

	private readonly TextBox title = new(), appId = new(), executable32 = new(), executable64 = new(), arguments = new(), dlcs = new();
	private readonly ComboBox architecture = new()
	{
		ItemsSource = new[]
		{
			"32-bit",
			"64-bit"
		}
	};
	private readonly TextBlock error = new()
	{
		TextWrapping = TextWrapping.Wrap,
		Margin = new Thickness(0, 8, 0, 0)
	};

	public GameDialog(Library library, Game game)
	{
		Title = "Settings · " + game.Name;
		Style = (Style)FindResource(typeof(Window));
		Width = 620;
		MinWidth = 500;
		MaxHeight = SystemParameters.WorkArea.Height;
		MinHeight = Math.Min(460, MaxHeight);
		SizeToContent = SizeToContent.Height;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		var root = new DockPanel
		{
			Margin = new Thickness(14)
		};
		var footer = new StackPanel();
		DockPanel.SetDock(footer, Dock.Bottom);
		footer.Children.Add(error);
		var actions = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 18, 0, 0)
		};
		var cancel = new Button
		{
			Content = "Cancel",
			IsCancel = true
		};
		var save = new Button
		{
			Content = "Save",
			IsDefault = true,
			Margin = new Thickness(0, 0, 9, 0)
		};
		actions.Children.Add(save);
		actions.Children.Add(cancel);
		footer.Children.Add(actions);
		root.Children.Add(footer);
		var form = new StackPanel
		{
			Margin = new Thickness(0, 0, 15, 0)
		};
		root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

		void Field(string label, UIElement control)
		{
			form.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 13, 0, 7) });
			form.Children.Add(control);
		}

		title.Text = game.Name;
		appId.Text = game.AppId == 0 ? "" : game.AppId.ToString();
		executable32.Text = game.Executable32;
		executable64.Text = game.Executable64;
		architecture.SelectedIndex = game.Prefer64Bit ? 1 : 0;
		arguments.Text = game.Arguments;
		dlcs.Text = game.DlcList;
		var lookupCancel = new CancellationTokenSource();
		Closed += (_, _) =>
		{
			lookupCancel.Cancel();
			lookupCancel.Dispose();
		};
		SteamMetadata? pendingMetadata = null;
		var find = new Button
		{
			Content = "Find on Steam",
			Margin = new Thickness(8, 0, 0, 0)
		};
		var nameRow = new DockPanel();
		DockPanel.SetDock(find, Dock.Right);
		nameRow.Children.Add(find);
		nameRow.Children.Add(title);
		Field("Name", nameRow);
		var lookupId = new Button
		{
			Content = "Look up ID",
			Margin = new Thickness(8, 0, 0, 0)
		};
		var idRow = new DockPanel();
		DockPanel.SetDock(lookupId, Dock.Right);
		idRow.Children.Add(lookupId);
		idRow.Children.Add(appId);
		Field("Steam App ID", idRow);
		AutomationProperties.SetName(title, "Game name or Steam App ID");
		AutomationProperties.SetName(appId, "Steam App ID");
		appId.ToolTip = "Enter or overwrite the App ID.";
		var steamMatches = new ListBox
		{
			MaxHeight = 105,
			Margin = new Thickness(0, 6, 0, 0),
			Visibility = game.SteamMatches.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
			ItemsSource = game.SteamMatches
		};
		form.Children.Add(steamMatches);
		var candidates = game.DetectedExecutables.Concat(game.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (Path: path, Architecture: Library.ExecutableArchitecture(library.GameFile(game, path)))).ToArray();

		void ExecutableField(string label, TextBox input, string machine)
		{
			AutomationProperties.SetName(input, label);
			var browse = new Button
			{
				Content = "Browse",
				Margin = new Thickness(8, 0, 0, 0)
			};
			var row = new DockPanel();
			DockPanel.SetDock(browse, Dock.Right);
			row.Children.Add(browse);
			row.Children.Add(input);
			Field(label, row);
			var matching = candidates.Where(candidate => candidate.Architecture == machine).Select(candidate => candidate.Path).ToList();
			if (matching.Count > 1)
			{
				var choices = new ComboBox
				{
					ItemsSource = matching,
					Margin = new Thickness(0, 6, 0, 0)
				};
				AutomationProperties.SetName(choices, label + " candidates");
				choices.SelectionChanged += (_, _) =>
				{
					if (choices.SelectedItem is string value)
						input.Text = value;
				};
				form.Children.Add(choices);
			}

			browse.Click += (_, _) =>
			{
				var picker = new OpenFileDialog
				{
					Title = "Choose the " + label.ToLowerInvariant(),
					Filter = "Windows executable|*.exe",
					InitialDirectory = library.GamePath(game)
				};
				if (picker.ShowDialog(this) != true)
					return;
				try
				{
					var relative = Path.GetRelativePath(library.GamePath(game), picker.FileName);
					var full = library.GameFile(game, relative);
					if (Emulator.Architecture(full) != machine)
						throw new IOException("This executable does not match the selected architecture.");
					input.Text = relative;
					error.Text = "";
				}
				catch (Exception ex)
				{
					error.Text = ex.Message;
				}
			};
		}

		ExecutableField("32-bit executable", executable32, "x86");
		ExecutableField("64-bit executable", executable64, "x64");
		Field("Launch by default", architecture);
		AutomationProperties.SetName(architecture, "Default launch architecture");
		Field("Additional launch arguments", arguments);
		dlcs.AcceptsReturn = true;
		dlcs.MinHeight = 80;
		dlcs.MaxHeight = 150;
		dlcs.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		Field("DLC list (one ID=Name per line)", dlcs);
		var refreshDlc = new Button
		{
			Content = "Discover DLC",
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 6, 0, 0)
		};
		form.Children.Add(refreshDlc);
		var lookingUp = false;

		void SetLookup(bool value)
		{
			lookingUp = value;
			find.IsEnabled = lookupId.IsEnabled = refreshDlc.IsEnabled = steamMatches.IsEnabled = save.IsEnabled = appId.IsEnabled = title.IsEnabled = dlcs.IsEnabled = !value;
		}

		async Task Lookup(uint id)
		{
			if (lookingUp)
				return;
			appId.Text = id.ToString();
			SetLookup(true);
			error.Text = "";
			try
			{
				var metadata = await library.Discovery.Fetch(id, lookupCancel.Token);
				var previous = pendingMetadata ?? Storage.Read<SteamMetadata>(library.SettingsFile(game, "Steam.json"));
				var draft = new Game
				{
					AppId = id,
					DlcList = dlcs.Text,
					Executable32 = executable32.Text,
					Executable64 = executable64.Text,
					Prefer64Bit = architecture.SelectedIndex == 1,
					DetectedExecutables = game.DetectedExecutables
				};
				SteamDiscovery.Merge(draft, metadata, previous);
				SteamDiscovery.SelectExecutable(library.GamePath(game), draft, metadata);
				pendingMetadata = metadata;
				appId.Text = id.ToString();
				title.Text = draft.Name;
				dlcs.Text = draft.DlcList;
				executable32.Text = draft.Executable32;
				executable64.Text = draft.Executable64;
				architecture.SelectedIndex = draft.Prefer64Bit ? 1 : 0;
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				error.Text = "Steam lookup failed: " + ex.Message.Split('\n')[0];
			}
			finally
			{
				SetLookup(false);
			}
		}

		steamMatches.SelectionChanged += async (_, _) =>
		{
			if (!lookingUp && steamMatches.SelectedItem is SteamMatch match)
			{
				title.Text = match.Name;
				await Lookup(match.AppId);
			}
		};
		find.Click += async (_, _) =>
		{
			if (lookingUp)
				return;
			if (uint.TryParse(title.Text.Trim(), out var id) && id != 0)
			{
				await Lookup(id);
				return;
			}

			SetLookup(true);
			error.Text = "";
			try
			{
				var matches = await library.Discovery.Search(title.Text.Trim(), lookupCancel.Token);
				steamMatches.ItemsSource = matches;
				steamMatches.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
				error.Text = matches.Count == 0 ? "No matching game found." : "";
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception)
			{
				error.Text = "Steam search unavailable.";
			}
			finally
			{
				SetLookup(false);
			}
		};

		async void LookupEnteredId(object sender, RoutedEventArgs e)
		{
			if (uint.TryParse(appId.Text.Trim(), out var id) && id != 0)
				await Lookup(id);
			else
				error.Text = "Enter a Steam App ID.";
		}

		lookupId.Click += LookupEnteredId;
		refreshDlc.Click += LookupEnteredId;

		string ValidateExecutable(TextBox input, string machine)
		{
			var relative = input.Text.Trim();
			if (relative.Length == 0)
				return "";
			var full = library.GameFile(game, relative);
			if (!File.Exists(full) || !Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
				throw new IOException("Choose an .exe inside this game's folder.");
			if (Emulator.Architecture(full) != machine)
				throw new IOException($"The {machine} executable has the wrong architecture.");
			return Path.GetRelativePath(library.GamePath(game), full);
		}

		void Save()
		{
			if (pendingMetadata == null && title.Text.Trim() == game.Name && appId.Text == (game.AppId == 0 ? "" : game.AppId.ToString()) && executable32.Text == game.Executable32 && executable64.Text == game.Executable64 && (architecture.SelectedIndex == 1) == game.Prefer64Bit && arguments.Text == game.Arguments && dlcs.Text == game.DlcList)
				return;
			library.EnsureNotRunning(game);
			if (string.IsNullOrWhiteSpace(title.Text))
				throw new IOException("Enter a display name.");
			uint id = 0;
			if (appId.Text.Trim().Length > 0 && !uint.TryParse(appId.Text.Trim(), out id))
				throw new IOException("Enter a numeric Steam App ID, or leave it blank.");
			var path32 = ValidateExecutable(executable32, "x86");
			var path64 = ValidateExecutable(executable64, "x64");
			if (string.IsNullOrWhiteSpace(architecture.SelectedIndex == 1 ? path64 : path32))
				throw new IOException("Choose an executable for the default launch architecture.");
			var draft = new Game
			{
				AppId = id,
				DlcList = dlcs.Text
			};
			var previous = pendingMetadata ?? Storage.Read<SteamMetadata>(library.SettingsFile(game, "Steam.json"));
			SteamDiscovery.Merge(draft, new SteamMetadata { AppId = id }, previous);
			var changed = game.Name != title.Text.Trim() || game.AppId != id || game.Executable32 != path32 || game.Executable64 != path64 || game.Prefer64Bit != (architecture.SelectedIndex == 1) || game.Arguments != arguments.Text || game.DlcList != draft.DlcList;
			if (!changed && pendingMetadata?.AppId != id)
				return;
			var updateEmulator = game.AppId != id || game.DlcList != draft.DlcList;
			if (game.AppId != id)
				game.SteamDataAppId = 0;
			game.Name = title.Text.Trim();
			game.AppId = id;
			game.Executable32 = path32;
			game.Executable64 = path64;
			if (id != 0)
				game.IsNonSteam = false;
			game.Prefer64Bit = architecture.SelectedIndex == 1;
			game.Arguments = arguments.Text;
			game.DlcList = draft.DlcList;
			if (pendingMetadata?.AppId == id)
			{
				Storage.Write(library.SettingsFile(game, "Steam.json"), pendingMetadata);
				game.SteamDataAppId = id;
			}

			library.SaveGame(game);
			Saved = true;
			if (updateEmulator)
				library.Emulator.UpdateSettings(game);
		}

		save.Click += (_, _) =>
		{
			try
			{
				Save();
				DialogResult = true;
			}
			catch (Exception ex)
			{
				error.Text = ex.Message;
			}
		};
		Content = root;
	}
}
