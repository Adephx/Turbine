using System.Windows;
using System.Windows.Media;

namespace Turbine;

public sealed record ThemeChoice(string Id, string Name, string ContrastLabel, string Background, string Foreground)
{
	public override string ToString() => $"{Name} ({ContrastLabel})";
}

public static class Themes
{
	public const string DefaultId = "steam";

	public static IReadOnlyList<ThemeChoice> Choices { get; } = Array.AsReadOnly<ThemeChoice>([
		new("acid-violet", "Acid violet", "14.8:1", "#B6FF00", "#260044"),
		new("berry", "Berry", "12.2:1", "#5A1425", "#FFF0F2"),
		new("blaze", "Blaze", "7.6:1", "#FF8A00", "#08172D"),
		new("charcoal", "Charcoal", "16.3:1", "#181818", "#F5F5F5"),
		new("cobalt", "Cobalt", "10.3:1", "#123A72", "#F1F5FF"),
		new("teal", "Deep teal", "9.0:1", "#064E50", "#E2FFFF"),
		new("electric-blue", "Electric blue", "13.5:1", "#111C72", "#F5FF55"),
		new("forest", "Forest", "14.1:1", "#10251D", "#E4F4E9"),
		new("hot-pink", "Hot pink", "7.2:1", "#FF69B4", "#070A30"),
		new("ivory", "Ivory", "15.3:1", "#FFF8E7", "#24201A"),
		new("lemon", "Lemon", "13.8:1", "#FFF1A8", "#292300"),
		new("midnight", "Midnight", "14.0:1", "#101D2F", "#DDEBFF"),
		new("mint", "Mint", "12.9:1", "#D7F5E5", "#112C1D"),
		new("neon-teal", "Neon teal", "10.4:1", "#00F2A9", "#062B23"),
		new("paper", "Paper", "16.4:1", "#F5F5F5", "#171717"),
		new("peach", "Peach", "12.0:1", "#FFD7C9", "#3B1710"),
		new("plum", "Plum", "12.7:1", "#3E164D", "#F9E9FF"),
		new("redline", "Redline", "7.1:1", "#FF7355", "#220700"),
		new("sky", "Sky", "12.4:1", "#D7F2FF", "#102D3A"),
		new(DefaultId, "Steam", "10.0:1", "#1B2838", "#C7D5E0"),
		new("ultraviolet", "Ultraviolet", "15.4:1", "#29004D", "#DFFF00"),
		new("voltage", "Voltage", "10.8:1", "#00F5FF", "#092D36"),
		new("yellow", "Warning Yellow", "14.9:1", "#FFD600", "#000000")
	]);

	public static string Normalize(string? id) => Choices.FirstOrDefault(theme => string.Equals(theme.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? DefaultId;

	public static void Apply(string? id)
	{
		var normalized = Normalize(id);
		var theme = Choices.First(theme => theme.Id == normalized);
		var resources = Application.Current.Resources;
		var background = Brush(theme.Background);
		var foreground = Brush(theme.Foreground);
		resources["ThemeBackground"] = background;
		resources["ThemeForeground"] = foreground;
		resources["ThemeSelection"] = foreground;
		resources["ThemeSelectionForeground"] = background;
	}

	private static SolidColorBrush Brush(string color)
	{
		var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
		brush.Freeze();
		return brush;
	}
}
