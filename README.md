# Noct

Noct is a local FLAC player for Windows. Add a music folder, choose an album or track, and control playback from the main window or a small panel at the top of the screen when the app is minimized. The app's interface is in English.

## Download

Download the ready-to-run packages from the [Noct 0.5.1 release](https://github.com/DikyMartin/Noct/releases/tag/v0.5.1).

Choose one of these packages:

| Package | How to use it |
| --- | --- |
| [Windows x64 installer](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/Noct-Setup-0.5.1-x64.exe) | Run the installer, then open Noct from the Start Menu. |
| [Windows x64 ZIP](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/Noct-0.5.1-win-x64.zip) | Extract the entire ZIP to a folder of your choice, then run `Noct.exe` inside it. |

[SHA256SUMS.txt](https://github.com/DikyMartin/Noct/releases/download/v0.5.1/SHA256SUMS.txt) contains checksums for both packages so you can check your downloads.

Both packages include the .NET Desktop Runtime. You do not need to install .NET or the SDK separately. GitHub's **Source code** ZIP contains the source files, not a ready-to-run app.

## Requirements

- Windows 11 x64. The packages target `win-x64`; Windows 10, ARM64, and other Windows versions have not been tested.
- A Windows audio output device. An ASIO driver is needed only if you choose ASIO.
- FLAC music files. Other audio formats and streaming services are not supported.

## Install and run

The installer installs Noct for the current user in `%LocalAppData%\Programs\Noct`, without administrator rights. A desktop shortcut is optional. Updates use the same location; close Noct before updating.

The ZIP package runs without installation. Keep all extracted files together with `Noct.exe`; copying only the EXE is not enough.

1. Open Noct and choose **Add folder** in Library.
2. Select a music folder. Noct scans it and its subfolders for FLAC files. Each folder that directly contains FLAC files appears as an album, using the folder's name as the album name.
3. Choose a track or album to play. Search is available in Library; you can view and edit the playback queue in Queue.
4. Choose an output device and mode in Settings if needed. WASAPI shared is the default.

Removing an album or source folder from Library removes only its record in Noct. Music files are not deleted or changed.

## Features

- Home, Now Playing, Library, Queue, and Settings; album and all-song views, search, and artwork from folders or FLAC files.
- Play/pause, previous/next, seek, volume, album shuffle, repeat, **Play next**, and **Add to queue**.
- Local `.lrc` lyrics: place `Song.lrc` beside `Song.flac`. Timestamped lyrics are highlighted during playback and can be clicked to seek. Noct does not download lyrics.
- A small panel at the top of the screen when minimized, with auto-hide and hover controls. Choose **Open Noct** from the panel or system tray to return. You can disable this feature in Settings. Close and the tray's **Exit** command close the app and stop playback.
- Saved library, queue, settings, and track position. The queue is restored when you reopen the app, without starting playback automatically.
- WASAPI shared, WASAPI exclusive, and optional ASIO output when a driver is available. Transparent mode requires WASAPI exclusive and locks software volume at unity gain.
- Gapless playback for tracks with the same format. Changing sample rate or format may require reopening the audio device.

WASAPI exclusive and Transparent mode do not prove bit-perfect output through a driver or DAC. DAC and ASIO hardware testing is still needed; see [verification results and limitations](docs/BUILD_NOTES.md).

## App data and uninstalling

Settings, the library index, artwork cache, and `noct.log` are stored in `%LocalAppData%\Noct`, including when you use the ZIP package. The ZIP package does not store settings beside the EXE.

Uninstalling through Windows Settings removes the program. The uninstaller offers a choice to remove Noct's data; music and lyrics files in your library folders are not included. For the ZIP package, close the app and delete the extracted folder. You can separately delete `%LocalAppData%\Noct` if you no longer need its data.

## Build from source

Use Windows x64, PowerShell, and the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Run these commands from the folder containing `Noct.slnx`:

```powershell
dotnet build Noct.slnx -c Release
dotnet run --project src/Noct.App/Noct.App.csproj -c Release --no-build
```

To run the tests, install the [official FLAC tools](https://xiph.org/flac/download.html) and make sure `flac.exe` and `metaflac.exe` are on `PATH`. These tools are needed only to generate test data, not to run Noct.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/make-test-audio.ps1
dotnet test tests/Noct.Tests/Noct.Tests.csproj -c Release
```

If the FLAC tools are not on `PATH`, pass their folder to the test data script using `-ToolDirectory`.

To create an installer and ZIP from the current source, install [Inno Setup 6](https://jrsoftware.org/isdl.php) from the version 6 downloads section, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/package.ps1
```

The output is in `dist\release`, together with `SHA256SUMS.txt`. The package version comes from `Directory.Build.props`. The script always publishes a fresh build rather than reusing old `dist` output. To create only a ZIP without Inno Setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/package.ps1 -ZipOnly
```

See the [development guide](docs/DEVELOPMENT.md) for the source layout, diagnostic tools, and isolated test data. The manual verification checklist is in [VALIDATION.md](docs/VALIDATION.md).

## License

A license for Noct's source code has not been selected. Dependency licenses do not automatically apply to Noct's source. Notices for the components used by Noct are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
