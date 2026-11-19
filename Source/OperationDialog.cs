using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Turbine;

public sealed class OperationDialog : Window
{
	private readonly TextBlock status = new()
	{
		TextAlignment = TextAlignment.Center,
		TextWrapping = TextWrapping.Wrap,
		MinHeight = 18
	};
	private readonly Button cancelButton = new()
	{
		Content = "Cancel",
		IsCancel = true,
		MinWidth = 90,
		HorizontalAlignment = HorizontalAlignment.Center,
		Margin = new Thickness(0, 16, 0, 0)
	};
	private readonly Action? cancel;
	private bool cancelling;
	private bool finished;

	public OperationDialog(string message, Action? cancel = null)
	{
		this.cancel = cancel;
		Title = "Turbine";
		WindowStyle = WindowStyle.SingleBorderWindow;
		status.Text = message;
		Style = (Style)FindResource(typeof(Window));
		Width = 340;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.CanResize;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		var layout = new StackPanel
		{
			Margin = new Thickness(16)
		};
		layout.Children.Add(status);
		var movement = new TranslateTransform();
		var marker = new Border
		{
			Width = 70,
			HorizontalAlignment = HorizontalAlignment.Left,
			RenderTransform = movement
		};
		marker.SetResourceReference(Border.BackgroundProperty, "ThemeForeground");
		var loading = new Border
		{
			Name = "LoadingBar",
			Height = 8,
			BorderThickness = new Thickness(1),
			Margin = new Thickness(0, 12, 0, 0),
			ClipToBounds = true,
			Child = marker
		};
		loading.SetResourceReference(Border.BorderBrushProperty, "ThemeForeground");
		loading.SetResourceReference(Border.BackgroundProperty, "ThemeBackground");
		loading.Loaded += (_, _) => movement.BeginAnimation(
			TranslateTransform.XProperty,
			new DoubleAnimation
			{
				From = 0,
				To = Math.Max(0, loading.ActualWidth - marker.Width - 2),
				Duration = TimeSpan.FromSeconds(1.2),
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever
			});
		loading.Unloaded += (_, _) => movement.BeginAnimation(TranslateTransform.XProperty, null);
		layout.Children.Add(loading);
		if (cancel != null)
			layout.Children.Add(cancelButton);
		cancelButton.Click += (_, _) => RequestCancel();
		Closing += (_, e) =>
		{
			if (!finished)
			{
				e.Cancel = true;
				RequestCancel();
			}
		};
		Content = layout;
	}

	private void RequestCancel()
	{
		if (cancel == null || cancelling || finished)
			return;
		cancelling = true;
		cancelButton.IsEnabled = false;
		status.Text = "Cancelling…";
		cancel();
	}

	public async Task Run(Func<IProgress<string>, Task> operation)
	{
		Task? running = null;
		var progress = new Progress<string>(message =>
		{
			if (!finished && !cancelling)
				status.Text = message;
		});

		async Task Execute()
		{
			try
			{
				await operation(progress);
			}
			finally
			{
				finished = true;
				_ = Dispatcher.BeginInvoke(new Action(Close));
			}
		}

		Loaded += (_, _) => running = Execute();
		ShowDialog();
		if (running != null)
			await running;
	}

	public async Task RunWhenNeeded(Func<Action, Task> operation)
	{
		var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			var running = operation(() => requested.TrySetResult());
			await Task.WhenAny(running, requested.Task);
			if (running.IsCompleted)
				await running;
			else
				await Run(_ => running);
		}
		finally
		{
			if (!finished)
			{
				finished = true;
				Close();
			}
		}
	}
}
