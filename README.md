# Turbine

Launcher for Steam games with integrated LAN and V-LAN support for co-op multiplayer.

## Build

Install the .NET 9 SDK and Visual Studio 2022 with **Desktop development with C++**, **MSVC v143** and a Windows SDK.

## Use

1. Run `Turbine.exe` and download the dependencies when prompted.
2. Place game folders under `Games` and press Refresh, or drag them into Turbine.
3. Right-click a game and choose Patch.
4. Double-click the game to play.


### V-LAN

Enter your ZeroTier network ID in Settings and save. Enable IPv4 assignment and broadcast on the network, and authorize Turbine's node in your ZeroTier controller. Set **V-LAN ON** for ZeroTier or **OFF** for local LAN only. Be sure to close running games before switching.

## Dependencies

- [Goldberg](https://github.com/Detanup01/gbe_fork) — Steam emulation.
- [Steamless](https://github.com/atom0s/Steamless) — SteamStub unpacking.
- [ZeroTier Sockets](https://github.com/zerotier/libzt/tree/1.8.4) — V-LAN networking.
- [Detours](https://github.com/microsoft/Detours/tree/v4.0.1) — Windows API interception.
- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam metadata.
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) — archive extraction.
- [protobuf-net](https://github.com/protobuf-net/protobuf-net), [ZstdSharp](https://github.com/oleg-st/ZstdSharp) and [System.IO.Hashing](https://github.com/dotnet/runtime) — runtime libraries.
