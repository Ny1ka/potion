# Potion Dropper for Windows

WinUI 3 (Windows App SDK) app on .NET 10. Windows 10 1809+; Windows 11 visuals (Mica) when available. It builds on
Windows only. On macOS, CI (`.github/workflows/ci.yml`) is the build check.

On a Windows PC with the .NET 10 SDK (or Visual Studio 2022+ with the "Windows application development" workload):

```powershell
dotnet build apps\dropper-windows\PotionDropper\PotionDropper.csproj -c Debug -p:Platform=x64
.\apps\dropper-windows\PotionDropper\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\PotionDropper.exe
```

Or open `Potion.slnx` at the repo root in Visual Studio and run the PotionDropper project.

Status (M5): the window has the link bar; Fetch checks links against the source allowlist (`docs/SOURCES.md`).
The media card, output options, download queue and Settings come in M7.

## What comes from where

- `PotionDesign.cs`, `PotionTheme.xaml` and `Assets/` are linked in place from `design-system/windows/`
  (`PotionDropper` assets); they're never copied or edited.
- `Potion.Core` (`core/dotnet/Potion.Core`) is a project reference: link parsing, the source allowlist, the
  downloader (`HttpClientTransport`) and `FFmpegMediaConverter`. Dropper doesn't need `Potion.Core.Windows`.
- The LGPL FFmpeg build is copied into an `ffmpeg` folder next to `PotionDropper.exe` (required for Release builds).
