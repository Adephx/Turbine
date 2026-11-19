# Turbine

A Steam games launcher with integrated LAN and V-LAN support for co-op multiplayer.

Runs on Windows 10/11 x64 with the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). Launches both 32-bit and 64-bit games.

## Build

Install the .NET 9 SDK and Visual Studio 2022 with **Desktop development with C++**, **MSVC v143** and a Windows SDK. Open `Turbine.sln` and build Release, or run:

```text
dotnet build Turbine.sln -c Release
```

## Use

1. Run `Turbine.exe` and download the dependencies when prompted.
2. Place game folders under `Games` and press Refresh, or drag them into Turbine.
3. Right-click a game and choose Patch.
4. Double-click the game to play.

Right-click a game for its settings, launch arguments and folder options. A check mark means ready; an X means unpatched.

Turbine moves to the tray when you launch a game or close the window. Click a game in the tray menu to play, right-click for options, or choose Exit to quit. Enable **Start with Windows** in Settings to start in the tray when you sign in.

### V-LAN

Enter your ZeroTier network ID in Settings and save. Enable IPv4 assignment and broadcast on the network, and authorize Turbine's node in your ZeroTier controller.

Set **V-LAN ON** for ZeroTier or **OFF** for local LAN. Turbine remembers the mode and connects on startup. Close running games before switching. Click the IP address to copy it.

When sharing Turbine, leave out the `Settings` folder so each player gets their own identity.

## Dependencies

- [Goldberg](https://github.com/Detanup01/gbe_fork) — Steam emulation.
- [Steamless](https://github.com/atom0s/Steamless) — SteamStub unpacking.
- [ZeroTier Sockets](https://github.com/zerotier/libzt/tree/1.8.4) — V-LAN networking.
- [Detours](https://github.com/microsoft/Detours/tree/v4.0.1) — Windows API interception.
- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam metadata.
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) — archive extraction.
- [protobuf-net](https://github.com/protobuf-net/protobuf-net), [ZstdSharp](https://github.com/oleg-st/ZstdSharp) and [System.IO.Hashing](https://github.com/dotnet/runtime) — runtime libraries.
