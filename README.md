# Portal Master Vault

Portal Master Vault is a Windows collection tracker for Skylanders figures. It reads supported Portals of Power, identifies scanned toys from local catalog data, and keeps a local editable collection with portraits, element art, scan animations, sounds, filters, and Discord Rich Presence.

## Current Status

This is an early release build. The app is usable for collection tracking, but catalog coverage and portal behavior are still being refined as more figures are tested.

Current catalog coverage:

- Spyro's Adventure
- Giants
- SWAP Force
- Trap Team
- SuperChargers
- Imaginators

Villain/trap display support exists in the codebase, but villains are currently hidden from the app while trap behavior is still being tested.

## Requirements

- Windows 10 or Windows 11
- Microsoft Edge WebView2 Runtime
- .NET 8 Desktop Runtime, unless using a future self-contained package
- A compatible Skylanders Portal of Power

Portal support depends on the USB driver exposed by Windows. HID and WinUSB portal paths are both handled by the desktop app.

Current portals supported are as follows:

- Spyro's Adventure (PS3, WiiU)
- Giants (PS3, WiiU)
- Swap Force (PS3, WiiU, Xbox 360)
- Trap Team (PS3, WiiU)
- Superchargers (PS3, WiiU)
- Imaginators (PS3, WiiU)

## Portal Scanning Issues

If the app isn't scanning your figures it's most likely a driver issues. I'm doing what I can to make all portals compatible but some things can be done on the user end to fix this.

## Zadig

Try using Zadig to change the drivers of your portal to WinUSB. Download Zadig, find your portal by going to options and selecting "List All Devices". Then find your device labelled "Spyro Porta". Then select the driver you wish to install to the portal, should be labelled as "WinUSB (v6.1.7600.16385)". Click "Replace Driver" and wait for the driver to finish installing. Once complete the portal should then succesfully work with the app. Don't worry this process won't break the original portal functionality and it should still work with the original games.

## Running A Release Build

The release build is produced at:

```text
desktop/SkylandersCollection.Desktop/bin/Release/net8.0-windows/
```

Run:

```text
SkylandersCollection.Desktop.exe
```

Keep the `app`, `assets`, `data`, and `runtimes` folders beside the executable. The app intentionally keeps JSON catalog files visible so they can be edited.

## Building From Source

Install the .NET 8 SDK, then run:

```powershell
dotnet build desktop/SkylandersCollection.Desktop/SkylandersCollection.Desktop.csproj -c Release
```

For a development build:

```powershell
dotnet build desktop/SkylandersCollection.Desktop/SkylandersCollection.Desktop.csproj -c Debug
```

Debug builds expose developer tools such as scan injection, figure dumps, and scanner diagnostics. Release builds hide those tools and disable normal browser-like WebView behavior such as context menus and text selection outside form controls.

## Data Storage

The app stores collection data in JSON next to the bundled data files. A fresh release starts with an empty collection.

Tracked source data lives under:

```text
data/catalog/
assets/
app/
desktop/SkylandersCollection.Desktop/
```

Build outputs, figure dumps, debug logs, local NuGet caches, and distribution zips are ignored by Git.

## Notes

This is a fan-made collection tool. Skylanders and related names, characters, and artwork belong to their respective owners.
