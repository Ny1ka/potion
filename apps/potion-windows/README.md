# Potion for Windows

WinUI 3 (Windows App SDK) app on .NET 10. Windows 10 1809+; Windows 11 visuals (Mica) when available.
It builds on Windows only. On macOS, CI (`.github/workflows/ci.yml`) is the build check.

On a Windows PC with the .NET 10 SDK (or Visual Studio 2022+ with the "Windows application development"
workload):

```powershell
dotnet build apps\potion-windows\Potion\Potion.csproj -c Debug -p:Platform=x64
.\apps\potion-windows\Potion\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Potion.exe
```

Or open `Potion.slnx` at the repo root in Visual Studio and run the Potion project.

## What comes from where

- `PotionDesign.cs`, `PotionTheme.xaml` and `Assets/` are linked in place from
  `design-system/windows/` (see `Potion.csproj`); they're never copied or edited.
- `Potion.Core` (`core/dotnet/Potion.Core`) is a project reference.
