using System.Windows;
using Forms = System.Windows.Forms;

namespace Turbine;

internal sealed class TrayIcon : IDisposable
{
	private readonly Forms.NotifyIcon icon;
	private readonly Forms.ContextMenuStrip menu = new()
	{
		ShowImageMargin = false,
		ShowCheckMargin = false,
		Renderer = new TrayMenuRenderer()
	};
	private readonly Forms.Timer availabilityTimer = new() { Interval = 150 };
	private readonly System.Drawing.Icon image;
	private readonly Action restore;
	private readonly Action exit;
	private readonly Action settings;
	private readonly Action changeTransport;
	private readonly Func<bool> useZeroTier;
	private readonly Func<bool> transportAvailable;
	private readonly Func<IEnumerable<Game>> games;
	private readonly Func<bool> available;
	private readonly Func<Game, System.Windows.Controls.ContextMenu> gameMenu;
	private readonly Func<Game, Task> playGame;
	private Action? refreshAvailability;

	public TrayIcon(Action restore, Action exit, Func<IEnumerable<Game>> games, Func<bool> available, Func<Game, System.Windows.Controls.ContextMenu> gameMenu, Action settings, Action changeTransport, Func<bool> useZeroTier, Func<bool> transportAvailable, Func<Game, Task> playGame)
	{
		this.restore = restore;
		this.exit = exit;
		this.settings = settings;
		this.changeTransport = changeTransport;
		this.useZeroTier = useZeroTier;
		this.transportAvailable = transportAvailable;
		this.games = games;
		this.available = available;
		this.gameMenu = gameMenu;
		this.playGame = playGame;
		using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Turbine;component/Turbine.ico")).Stream;
		using var resource = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
		image = (System.Drawing.Icon)resource.Clone();
		icon = new Forms.NotifyIcon
		{
			Text = "Turbine",
			Icon = image,
			ContextMenuStrip = menu
		};
		icon.DoubleClick += (_, _) => restore();
		menu.Opening += (_, e) =>
		{
			BuildMenu();
			e.Cancel = false;
		};
		menu.Opened += (_, _) => availabilityTimer.Start();
		menu.Closed += (_, _) => availabilityTimer.Stop();
		availabilityTimer.Tick += (_, _) => refreshAvailability?.Invoke();
	}

	public bool Visible
	{
		get => icon.Visible; set => icon.Visible = value;
	}

	private void BuildMenu()
	{
		while (menu.Items.Count > 0)
			menu.Items[0].Dispose();
		menu.Items.Add("Open", null, (_, _) => restore());
		var settingsItem = menu.Items.Add("Settings", null, (_, _) =>
		{
			if (available())
				settings();
		});
		var transport = new Forms.ToolStripMenuItem();
		transport.Click += (_, _) =>
		{
			if (available() && transportAvailable())
				changeTransport();
		};
		menu.Items.Add(transport);
		menu.Items.Add(new Forms.ToolStripSeparator());
		var entries = games().Where(game => game.IsReady).OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		foreach (var game in entries)
		{
			var item = new GameMenuItem(game, async () =>
			{
				if (!GameAvailable(game))
					return;
				menu.Close();
				await playGame(game);
			}, () => GameAvailable(game));
			AddGameActions(item, game);
			menu.Items.Add(item);
		}

		if (entries.Length == 0)
			menu.Items.Add(new Forms.ToolStripMenuItem("No ready games") { Enabled = false });
		menu.Items.Add(new Forms.ToolStripSeparator());
		var exitItem = menu.Items.Add("Exit", null, (_, _) =>
		{
			if (available())
				exit();
		});

		refreshAvailability = () =>
		{
			var enabled = available();
			settingsItem.Enabled = exitItem.Enabled = enabled;
			transport.Enabled = enabled && transportAvailable();
			transport.Text = useZeroTier() ? "V-LAN: ON" : "V-LAN: OFF";
			var currentGames = enabled ? games().ToHashSet() : null;
			foreach (var item in menu.Items.OfType<GameMenuItem>())
				item.Enabled = currentGames?.Contains(item.Game) == true && item.Game.IsReady;
		};
		refreshAvailability();
	}

	private bool GameAvailable(Game game) => available() && game.IsReady && games().Contains(game);

	private void AddGameActions(Forms.ToolStripMenuItem item, Game game)
	{
		var actions = gameMenu(game);
		foreach (var entry in actions.Items)
		{
			if (entry is System.Windows.Controls.Separator)
				item.DropDownItems.Add(new Forms.ToolStripSeparator());
			else if (entry is System.Windows.Controls.MenuItem action)
			{
				action.ApplyTemplate();
				var child = new Forms.ToolStripMenuItem((action.Header?.ToString() ?? "").Replace("&", "&&"))
				{
					Enabled = action.IsEnabled
				};
				child.Click += (_, _) =>
				{
					if (!GameAvailable(game) || !action.IsEnabled)
						return;
					menu.Close();
					action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
				};
				item.DropDownItems.Add(child);
			}
		}

		if (item.DropDown is Forms.ToolStripDropDownMenu dropDown)
		{
			dropDown.ShowImageMargin = false;
			dropDown.ShowCheckMargin = false;
		}
		item.DropDown.RenderMode = Forms.ToolStripRenderMode.System;
	}

	private sealed class TrayMenuRenderer : Forms.ToolStripSystemRenderer
	{
		protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
		{
			if (e.Item is not GameMenuItem)
				base.OnRenderArrow(e);
		}
	}

	private sealed class GameMenuItem : Forms.ToolStripMenuItem
	{
		private readonly Action play;
		private Forms.MouseButtons pressedButton;
		private bool openingActions;

		public Game Game { get; }

		public GameMenuItem(Game game, Action play, Func<bool> available) : base(game.Name.Replace("&", "&&"))
		{
			Game = game;
			this.play = play;
			DropDown.Opening += (_, e) => e.Cancel = !openingActions || !available();
		}

		protected override void OnMouseDown(Forms.MouseEventArgs e)
		{
			// The default handler opens the submenu on mouse down.
			pressedButton = e.Button;
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			pressedButton = Forms.MouseButtons.None;
			base.OnMouseLeave(e);
		}

		protected override void OnMouseUp(Forms.MouseEventArgs e)
		{
			var button = pressedButton;
			pressedButton = Forms.MouseButtons.None;
			if (!Enabled || e.Button != button)
				return;

			if (button == Forms.MouseButtons.Left)
				play();
			else if (button == Forms.MouseButtons.Right)
				OpenActions(false);
		}

		protected override bool ProcessDialogKey(Forms.Keys keyData)
		{
			if (keyData is Forms.Keys.Enter or Forms.Keys.Space)
			{
				if (Enabled)
					play();
				return true;
			}

			if (keyData is Forms.Keys.Right or Forms.Keys.Apps or (Forms.Keys.Shift | Forms.Keys.F10))
			{
				if (Enabled)
					OpenActions(true);
				return true;
			}

			return base.ProcessDialogKey(keyData);
		}

		private void OpenActions(bool selectFirst)
		{
			if (Owner != null)
				foreach (var item in Owner.Items.OfType<GameMenuItem>())
					if (item != this)
						item.HideDropDown();

			openingActions = true;
			try
			{
				if (selectFirst)
					base.ProcessDialogKey(Forms.Keys.Enter);
				else
					ShowDropDown();
			}
			finally
			{
				openingActions = false;
			}
		}
	}

	public void Dispose()
	{
		availabilityTimer.Stop();
		icon.Visible = false;
		icon.Dispose();
		menu.Dispose();
		availabilityTimer.Dispose();
		image.Dispose();
	}
}
