# 09 · Platforms

Same design, expressed through each platform's own conventions. When a platform convention conflicts with something in these docs, the platform convention wins, unless it touches the logo or the meaning of the category colors.

## macOS

**Stack:** SwiftUI, with AppKit bridges where needed (drag-and-drop of folders, Dock progress, menu bar extra). macOS 13+.

| Topic | Do |
|---|---|
| Window | `WindowGroup` with `.windowToolbarStyle(.unifiedCompact)`, default size 720 × 520, min 560 × 420. Title "Potion". |
| Background | System window background. The drop zone sits directly on it, with no card. |
| Toolbar | "Add Files" (`plus`) and Settings (`gearshape`). Use `.help()` tooltips everywhere. |
| Lists | `List` / `Table` with native selection, inset style. |
| Convert sheet | Standalone `Window` scene (from Quick Action) or `.sheet` (from inside the app). Return/Esc work by default. |
| Settings | `Settings { }` scene, tabs General / Output / Advanced. ⌘, opens it. |
| Menus | Standard app menu plus File › Add Files… (⌘O), Convert All (⌘↩), Remove (⌫), Show in Finder (⌘⇧R). |
| Dock | Badge with the remaining count during a batch (`NSApp.dockTile.badgeLabel`). |
| Notifications | `UNUserNotificationCenter` on completion when the app isn't active. |
| Quick Action | Finder Action Extension, "Convert with Potion". Icon: mono mark. |
| Menu bar extra | `MenuBarExtra` with `Image("MenuBarIcon")` (template), off by default. |
| App icon | `AppIcon` from the generated catalog. Don't add Big Sur-style gloss: it's already flat. |
| Colors | System colors for chrome. `PotionColor.*` / `PotionCategory.*` for custom views (`03-color.md`). |
| Vibrancy | Sidebar/toolbar materials are fine. Never put category colors on top of vibrancy at low opacity. |
| Increase Contrast | Check `@Environment(\.colorSchemeContrast)` and switch custom borders to `borderStrong`. |

**Asset catalog contents** (`macos/<App>/Assets.xcassets`): `AppIcon`, `LogoMark` (SVG, preserves vector data, light/dark), `MenuBarIcon` (template PNG @1x/@2x), and colorsets `Potion{Background, Surface, SurfaceRaised, SurfaceSunken, Border, BorderStrong, TextPrimary, TextSecondary, TextTertiary, TextDisabled, Success, Warning, Error, Scrim, Ink}` and `Category{Image,Audio,Video,Docs}{Fill,Text,Tint,OnFill}`.

## Windows

**Stack:** WinUI 3 (Windows App SDK). Windows 10 1809+ minimum; Windows 11 visuals (Mica, rounded corners) when available.

| Topic | Do |
|---|---|
| Window | Default 820 × 580, min 600 × 440. `SystemBackdrop = new MicaBackdrop()`. `ExtendsContentIntoTitleBar = true` with a custom title bar that holds the small mark (16 px, `Square44x44Logo.targetsize-16`) and "Potion". |
| Window icon | `AppWindow.SetIcon("Assets/Potion.ico")`. |
| Lists | `ListView` with `AddDeleteThemeTransition`. Row template per `06-components.md`. |
| Convert dialog | `ContentDialog`: PrimaryButtonText "Convert", CloseButtonText "Cancel", DefaultButton Primary. |
| Format picker | `ComboBox` grouped via `CollectionViewSource`, or `DropDownButton` with a `MenuFlyout`. |
| Errors | `InfoBar` (Severity Error/Warning) above the list. |
| Settings | Its own page. Toolkit `SettingsCard`/`SettingsExpander` rows are a good fit. |
| Taskbar | Progress via `ITaskbarList3::SetProgressValue` / `SetProgressState` (error state on failure). |
| Notifications | App notifications (`AppNotificationManager`) with a "Show in folder" button. |
| Context menu | `IExplorerCommand` handler, "Convert with Potion", icon `Potion.ico`. Needs package identity (MSIX or sparse package) to appear in the Windows 11 top-level menu. |
| Tray (optional) | `Tray-dark-taskbar.ico` / `Tray-light-taskbar.ico`. Swap on `UISettings.ColorValuesChanged`, based on the taskbar theme (`SystemUsesLightTheme` registry value). |
| Theme | Follow the system. `PotionTheme.xaml` ThemeDictionaries handle Dark ("Default"), Light and HighContrast. |
| Accent | `AccentFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush`, etc. Never a category brush. |
| Corners | Use `ControlCornerRadius` (4) / `OverlayCornerRadius` (8) for stock-like elements, and Potion radius tokens for tiles and the drop zone. |

**MSIX manifest pointers** (`windows/<App>/Assets`):

```xml
<uap:VisualElements DisplayName="Potion" Square150x150Logo="Assets\Square150x150Logo.png"
    Square44x44Logo="Assets\Square44x44Logo.png" BackgroundColor="transparent" Description="Potion">
  <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
  <uap:SplashScreen Image="Assets\SplashScreen.png" BackgroundColor="#0E0C14" />
</uap:VisualElements>
<!-- Properties: <Logo>Assets\StoreLogo.png</Logo> -->
```

The `.scale-*`, `.targetsize-*`, `_altform-unplated` and `_altform-lightunplated` files are picked up automatically by MRT.

## Shared logic, separate UI

Both platforms consume the same `tokens.json`, `formats.json` and copy rules. Keep string tables aligned (same keys), with platform-specific variants only where `01-brand.md` lists a wording difference.
