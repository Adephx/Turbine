using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Turbine.Networking;

internal static class LanAddress
{
	private const byte HardwareInterface = 1;
	private const byte FilterInterface = 2;

	internal static IPAddress? GetPrimaryIPv4()
	{
		IPAddress? bestAddress = null;
		uint bestMetric = uint.MaxValue;
		nint stack = GetIfStackTable(out nint table) == 0 ? table : 0;
		try
		{
			foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (adapter.OperationalStatus != OperationalStatus.Up ||
				    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
					continue;

				IPInterfaceProperties properties = adapter.GetIPProperties();
				var address = properties.UnicastAddresses.FirstOrDefault(address =>
					address.Address.AddressFamily == AddressFamily.InterNetwork &&
					address.PrefixLength > 0 &&
					address.DuplicateAddressDetectionState >= DuplicateAddressDetectionState.Deprecated &&
					!IPAddress.IsLoopback(address.Address) && !address.Address.Equals(IPAddress.Any));
				if (address is null)
					continue;

				int index = properties.GetIPv4Properties()?.Index ?? 0;
				if (index == 0 || !HasLanLink(index, stack))
					continue;

				var row = new IpInterfaceRow { Family = (ushort)AddressFamily.InterNetwork, Index = index };
				if (GetIpInterfaceEntry(ref row) == 0 && (bestAddress is null || row.Metric < bestMetric))
				{
					bestAddress = address.Address;
					bestMetric = row.Metric;
				}
			}
		}
		catch (NetworkInformationException)
		{
			return null;
		}
		finally
		{
			if (stack != 0)
				FreeMibTable(stack);
		}
		return bestAddress;
	}

	// Match the physical Ethernet/Wi-Fi and layered link rules in Native/Lan.cpp.
	private static bool HasLanLink(int index, nint stack, int depth = 0)
	{
		var row = new InterfaceRow { Index = index };
		if (depth > 8 || GetIfEntry2(ref row) != 0 || row.Status != OperationalStatus.Up)
			return false;
		if ((row.Flags & HardwareInterface) != 0 &&
		    row.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
			return true;
		if (stack == 0 || (row.Flags & FilterInterface) != 0 || row.TunnelType != 0)
			return false;

		int count = Marshal.ReadInt32(stack);
		for (int i = 0, offset = sizeof(int); i < count; i++, offset += 2 * sizeof(int))
		{
			// Each MIB_IFSTACK_ROW contains a higher and lower interface index.
			if (Marshal.ReadInt32(stack, offset) == index &&
			    HasLanLink(Marshal.ReadInt32(stack, offset + sizeof(int)), stack, depth + 1))
				return true;
		}
		return false;
	}

	[DllImport("iphlpapi.dll", ExactSpelling = true)]
	private static extern uint GetIfStackTable(out nint table);

	[DllImport("iphlpapi.dll", ExactSpelling = true)]
	private static extern void FreeMibTable(nint table);

	[DllImport("iphlpapi.dll", ExactSpelling = true)]
	private static extern uint GetIfEntry2(ref InterfaceRow row);

	[DllImport("iphlpapi.dll", ExactSpelling = true)]
	private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);

	// Windows SDK MIB_IF_ROW2 layout with only the fields used here exposed.
	[StructLayout(LayoutKind.Explicit, Size = 1352)]
	private struct InterfaceRow
	{
		[FieldOffset(8)] internal int Index;
		[FieldOffset(1128)] internal NetworkInterfaceType Type;
		[FieldOffset(1132)] internal int TunnelType;
		[FieldOffset(1152)] internal byte Flags;
		[FieldOffset(1156)] internal OperationalStatus Status;
	}

	// Windows SDK MIB_IPINTERFACE_ROW layout with only the fields used here exposed.
	[StructLayout(LayoutKind.Explicit, Size = 168)]
	private struct IpInterfaceRow
	{
		[FieldOffset(0)] internal ushort Family;
		[FieldOffset(16)] internal int Index;
		[FieldOffset(148)] internal uint Metric;
	}
}
