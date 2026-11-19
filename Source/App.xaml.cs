using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Turbine;

public partial class App : Application
{
	private Mutex? instance;
	private bool ownsInstance;
	private NamedPipeServerStream? activationPipe;
	private readonly CancellationTokenSource activationCancellation = new();

	internal bool StartInTray { get; private set; }

	public static readonly DependencyProperty CustomCaptionProperty = DependencyProperty.RegisterAttached(
		"CustomCaption", typeof(bool), typeof(App), new PropertyMetadata(false, OnCustomCaptionChanged));

	public static bool GetCustomCaption(DependencyObject window) => (bool)window.GetValue(CustomCaptionProperty);

	public static void SetCustomCaption(DependencyObject window, bool value) => window.SetValue(CustomCaptionProperty, value);

	public static readonly DependencyProperty ShowCaptionMarkProperty = DependencyProperty.RegisterAttached(
		"ShowCaptionMark", typeof(bool), typeof(App), new PropertyMetadata(false));

	public static bool GetShowCaptionMark(DependencyObject window) => (bool)window.GetValue(ShowCaptionMarkProperty);

	public static void SetShowCaptionMark(DependencyObject window, bool value) => window.SetValue(ShowCaptionMarkProperty, value);

	public static Thickness MaximizedFramePadding
	{
		get
		{
			const int paddedBorder = 92;
			const uint logicalDpi = 96;
			var frame = SystemParameters.WindowResizeBorderThickness;
			// WPF's resize frame excludes Windows' padded border.
			var padding = GetSystemMetricsForDpi(paddedBorder, logicalDpi);
			return new Thickness(frame.Left + padding, frame.Top + padding, frame.Right + padding, frame.Bottom + padding);
		}
	}

	[DllImport("user32.dll")]
	private static extern int GetSystemMetricsForDpi(int index, uint dpi);

	private static void OnCustomCaptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
	{
		if (sender is not Window window || !(bool)e.NewValue)
			return;
		foreach (var command in new[] { SystemCommands.CloseWindowCommand, SystemCommands.MinimizeWindowCommand, SystemCommands.MaximizeWindowCommand, SystemCommands.RestoreWindowCommand })
			window.CommandBindings.Add(new CommandBinding(command, ExecuteCaptionCommand, CanExecuteCaptionCommand));
	}

	private static void CanExecuteCaptionCommand(object sender, CanExecuteRoutedEventArgs e)
	{
		var window = (Window)sender;
		e.CanExecute = e.Command == SystemCommands.CloseWindowCommand ||
			(e.Command == SystemCommands.MinimizeWindowCommand && window.ResizeMode != ResizeMode.NoResize) ||
			(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip);
		e.Handled = true;
	}

	private static void ExecuteCaptionCommand(object sender, ExecutedRoutedEventArgs e)
	{
		var window = (Window)sender;
		if (e.Command == SystemCommands.CloseWindowCommand)
			SystemCommands.CloseWindow(window);
		else if (e.Command == SystemCommands.MinimizeWindowCommand)
			SystemCommands.MinimizeWindow(window);
		else if (e.Command == SystemCommands.MaximizeWindowCommand)
			SystemCommands.MaximizeWindow(window);
		else if (e.Command == SystemCommands.RestoreWindowCommand)
			SystemCommands.RestoreWindow(window);
		e.Handled = true;
	}

	public App()
	{
		RuntimeDependencies.Register(AppContext.BaseDirectory);
		InputManager.Current.PreProcessInput += UpdateKeyboardFocusVisibility;
	}

	private void UpdateKeyboardFocusVisibility(object sender, PreProcessInputEventArgs e)
	{
		var input = e.StagingItem.Input.RoutedEvent;
		var visibility = Visibility.Visible;
		if (input != Keyboard.PreviewKeyDownEvent)
		{
			if (input != Mouse.PreviewMouseDownEvent && input != Stylus.PreviewStylusDownEvent && input != UIElement.PreviewTouchDownEvent)
				return;
			if (!SystemParameters.KeyboardCues)
				visibility = Visibility.Collapsed;
		}

		const string resourceKey = "KeyboardFocusVisibility";
		if (Resources[resourceKey] is Visibility current && current != visibility)
			Resources[resourceKey] = visibility;
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		StartInTray = e.Args.Contains(WindowsStartup.Argument, StringComparer.OrdinalIgnoreCase);
		var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant())));
		var pipeName = $"TURBINE-{Process.GetCurrentProcess().SessionId}-{identity}";
		instance = new Mutex(false, "Local\\TURBINE-" + identity);
		var wait = Stopwatch.StartNew();
		while (!(ownsInstance = TryOwnInstance()))
		{
			if (StartInTray)
			{
				Shutdown();
				return;
			}
			if (ActivateExistingInstance(pipeName).GetAwaiter().GetResult())
			{
				Shutdown();
				return;
			}

			if (wait.Elapsed >= TimeSpan.FromSeconds(5))
			{
				NoticeDialog.Show(null, "Could not activate Turbine. Try again.");
				Shutdown();
				return;
			}
		}

		activationPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		_ = ListenForActivation(activationPipe, activationCancellation.Token);
		base.OnStartup(e);
		var window = new MainWindow();
		MainWindow = window;
		if (StartInTray)
		{
			window.ShowActivated = false;
			window.ShowInTaskbar = false;
			window.WindowState = WindowState.Minimized;
		}
		window.Show();
	}

	private bool TryOwnInstance()
	{
		try
		{
			return instance!.WaitOne(0);
		}
		catch (AbandonedMutexException)
		{
			return true;
		}
	}

	private static async Task<bool> ActivateExistingInstance(string pipeName)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
		using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
			if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
				AllowSetForegroundWindow(processId);
			await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
			var reply = new byte[1];
			return await pipe.ReadAsync(reply, timeout.Token).ConfigureAwait(false) == 1 && reply[0] == 1;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			return false;
		}
	}

	private async Task ListenForActivation(NamedPipeServerStream pipe, CancellationToken cancel)
	{
		try
		{
			while (!cancel.IsCancellationRequested)
			{
				await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
				try
				{
					using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
					timeout.CancelAfter(TimeSpan.FromSeconds(2));
					var request = new byte[1];
					if (await pipe.ReadAsync(request, timeout.Token).ConfigureAwait(false) == 1 && request[0] == 1)
					{
						_ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
						{
							if (MainWindow is MainWindow window)
								window.RestoreWindow();
						}));
						await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
					}
				}
				catch (Exception ex) when (ex is IOException or OperationCanceledException)
				{
				}
				finally
				{
					if (!cancel.IsCancellationRequested)
						pipe.Disconnect();
				}
			}
		}
		catch (Exception ex) when (cancel.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException)
		{
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		InputManager.Current.PreProcessInput -= UpdateKeyboardFocusVisibility;
		activationCancellation.Cancel();
		activationPipe?.Dispose();
		activationCancellation.Dispose();
		if (ownsInstance)
			instance?.ReleaseMutex();
		instance?.Dispose();
		base.OnExit(e);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AllowSetForegroundWindow(uint processId);

	protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
	{
		if (MainWindow is MainWindow window)
			e.Cancel = !window.ExitApplication(sessionEnding: true);
		base.OnSessionEnding(e);
	}
}
