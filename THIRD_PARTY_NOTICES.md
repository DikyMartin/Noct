# Third-party notices

This file describes dependencies used by Noct. It does not assign a license to Noct's own source code.

## NAudio 3.3.0

Noct uses `NAudio.Core`, `NAudio.Wasapi`, and `NAudio.Asio` 3.3.0 for Windows audio output. Their restored NuGet metadata declares the MIT license and points to repository commit `4f8889e2bd34c3978a25652c6c172fa1502ccba3`.

The copyright and complete permission notice from that commit are included in [licenses/NAudio/LICENSE.txt](licenses/NAudio/LICENSE.txt). Project: [NAudio](https://github.com/naudio/NAudio).

## .NET runtime

Self-contained packages include the .NET and Windows Desktop runtimes. Packaging copies their license and available third-party notices from the matching restored runtime packs into `licenses/Microsoft.NETCore.App/` and `licenses/Microsoft.WindowsDesktop.App/`. Those files travel with both the ZIP and installer.

## Development tools

xUnit, its Visual Studio runner, and Microsoft.NET.Test.Sdk are used by the test project and are not shipped in the application package. Xiph's `flac` and `metaflac` generate reference test material and are not shipped. Inno Setup compiles the installer; the compiler is not part of the installed application.
