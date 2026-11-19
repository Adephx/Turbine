using System.Globalization;

namespace Turbine;

public static class BroadcastNetworks
{
	public static bool IsValidAddress(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		var parts = value.Split('.');
		if (parts.Length != 4)
		{
			return false;
		}

		Span<byte> octets = stackalloc byte[4];
		for (var i = 0; i < parts.Length; i++)
		{
			if (parts[i].Length == 0 || !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]) || parts[i] != octets[i].ToString(CultureInfo.InvariantCulture))
			{
				return false;
			}
		}

		return octets[0] != 0 && octets[0] != 127 && octets[0] < 224;
	}
}
