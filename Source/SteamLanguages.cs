namespace Turbine;

public sealed record SteamLanguage(string Code, string DisplayName);

public static class SteamLanguages
{
	public static IReadOnlyList<SteamLanguage> All { get; } = Array.AsReadOnly<SteamLanguage>([
		new("arabic", "Arabic"),
		new("bulgarian", "Bulgarian"),
		new("schinese", "Chinese (Simplified)"),
		new("tchinese", "Chinese (Traditional)"),
		new("czech", "Czech"),
		new("danish", "Danish"),
		new("dutch", "Dutch"),
		new("english", "English"),
		new("finnish", "Finnish"),
		new("french", "French"),
		new("german", "German"),
		new("greek", "Greek"),
		new("hungarian", "Hungarian"),
		new("indonesian", "Indonesian"),
		new("italian", "Italian"),
		new("japanese", "Japanese"),
		new("koreana", "Korean"),
		new("malay", "Malay"),
		new("norwegian", "Norwegian"),
		new("polish", "Polish"),
		new("portuguese", "Portuguese"),
		new("brazilian", "Portuguese (Brazil)"),
		new("romanian", "Romanian"),
		new("russian", "Russian"),
		new("spanish", "Spanish (Spain)"),
		new("latam", "Spanish (Latin America)"),
		new("swedish", "Swedish"),
		new("thai", "Thai"),
		new("turkish", "Turkish"),
		new("ukrainian", "Ukrainian"),
		new("vietnamese", "Vietnamese")
	]);

	public static bool IsSupported(string? code) => Find(code) != null;

	public static string Normalize(string? code) => Find(code)?.Code ?? "english";

	private static SteamLanguage? Find(string? code)
	{
		var trimmed = code?.Trim();
		return All.FirstOrDefault(language => string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase));
	}
}
