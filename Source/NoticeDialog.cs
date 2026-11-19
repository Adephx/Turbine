using System.Windows;
using System.Windows.Controls;

namespace Turbine;

public sealed class NoticeDialog : Window
{
	private NoticeDialog(Window? owner, string message, string? accept, string reject)
	{
		Owner = owner;
		Title = "Turbine";
		WindowStyle = WindowStyle.SingleBorderWindow;
		Style = (Style)FindResource(typeof(Window));
		SizeToContent = SizeToContent.WidthAndHeight;
		MaxWidth = Math.Min(480, SystemParameters.WorkArea.Width * 0.8);
		MaxHeight = SystemParameters.WorkArea.Height * 0.8;
		ResizeMode = ResizeMode.CanResize;
		ShowInTaskbar = false;
		WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
		var layout = new Grid
		{
			Margin = new Thickness(16)
		};
		layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		layout.Children.Add(new ScrollViewer
		{
			Focusable = false,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = MaxWidth - 48 }
		});
		var actions = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 16, 0, 0)
		};
		Grid.SetRow(actions, 1);
		var okay = new Button
		{
			Content = accept ?? "OK",
			MinWidth = 90,
			IsDefault = accept == null,
			IsCancel = accept == null
		};
		okay.Click += (_, _) => DialogResult = true;
		actions.Children.Add(okay);
		if (accept != null)
			actions.Children.Add(new Button { Content = reject, MinWidth = 90, IsCancel = true, IsDefault = true, Margin = new Thickness(10, 0, 0, 0) });
		layout.Children.Add(actions);
		Content = layout;
	}

	public static bool Show(Window? owner, string message, string? accept = null, string reject = "Cancel") => new NoticeDialog(owner, message, accept, reject).ShowDialog() == true;
}
