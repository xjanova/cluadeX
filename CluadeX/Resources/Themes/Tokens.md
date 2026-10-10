# CluadeX theme tokens

The colour, brush, effect and font vocabulary every theme defines. The list in code is
`Services/ThemeTokens.cs` (`ThemeTokens.All` = the semantic tokens below, `ThemeTokens.Legacy` = the old palette
keys still in use); `tests/themetest` fails any theme that misses a key or has a key the others do not.

Themes: **Midnight** (default — the look before themes existed, must stay pixel-identical), **Classic** (Windows XP
Silver / Luna) and **Neon Cyber LED**. Owner-approved designs: `E:\cluadex-bld\research\themes\*.dc.html`.

## How it works

```
App.xaml  Application.Resources.MergedDictionaries
  [0] Resources/Themes/Midnight.xaml   ← THE THEME SLOT: every token + the legacy palette. ThemeService.Apply swaps it.
  [1] Resources/Theme.xaml             ← shared control styles, colour-free: {DynamicResource token} everywhere
```

* `ThemeService` (DI singleton, `ThemeService.Current` for code outside DI): `Apply(id)` replaces entry [0] with
  `Resources/Themes/<id>.xaml` — live, no restart — saves `AppSettings.Theme` and raises `ThemeChanged`.
  `ApplySaved()` runs in `App.OnStartup` before the main window exists; an unknown / tampered value starts Midnight.
* `{DynamicResource X}` follows a theme switch immediately. `{StaticResource X}` is resolved once, when the view is
  built — those views pick a new theme up the next time they are built (a page switch rebuilds a page). That is why
  converting a view means **StaticResource → DynamicResource**.
* Picker: Settings › General › Appearance (`Views/Settings/ThemePicker.xaml`, `SettingsViewModel.ThemeOptions` /
  `SelectThemeCommand`). `ThemeInfo.IsReady=false` shows an "In progress" badge on a card — flip it in
  `ThemeService.Themes` when a theme is finished.

## Rules for the converter teams

1. **Use semantic tokens, never a legacy key**, and use them with `DynamicResource`:
   `Foreground="{DynamicResource Text.Primary}"`. A legacy key (`NeonInk0Brush`, `TextBrush`, `Surface0Brush` …) is
   only kept so unconverted views keep working.
2. **The local-override trap.** Seven views redefine the Catppuccin keys locally in `UserControl.Resources`
   (ChatView, FeaturesView, McpServersView, ModelManagerView, PermissionsView, SettingsView, TaskManagerView — the
   "Neon retheme — local override" block). A DynamicResource lookup starts at the element, so it finds that LOCAL copy
   first. When you convert such a view: map every key through the *local block* table below (the value the view really
   shows) and delete the local block when the last reference is gone. Never add a token name to a local block.
3. **Setter values take whole tokens.** In `Style`/`ControlTemplate` setters and triggers write
   `Value="{DynamicResource Glow.Led.Success}"` (a whole brush / effect token) — never an inline
   `<DropShadowEffect Color="{DynamicResource …}"/>` or `<SolidColorBrush Color="{DynamicResource …}"/>`: a style
   seals its setter values and a sealed Freezable cannot follow a resource (it silently renders black). Directly on an
   element (not in a style) prefer the attribute form too: `Effect="{DynamicResource Shadow.Card}"`,
   `Background="{DynamicResource TitleBar.Background}"`.
4. **A colour with no token**: pick the nearest token when the table below says `exact` or `≈ ΔN` (N ≤ 8 keeps the
   ≤ 0.1 % golden check green); otherwise add a token — to **all three** theme files, to this file and to
   `ThemeTokens.cs` (themetest T2 fails a theme key that is in neither `ThemeTokens.All` nor `.Legacy`) — with the
   exact Midnight value.
5. **C#**: an element property set from code → `element.SetResourceReference(Prop, "Token")` (follows switches by
   itself). A brush handed to a binding or drawn (converters, VMs, `OnRender`) → `ThemeService.BrushOf("Token")` /
   `ColorOf("Token", fallback)`, and refresh on `ThemeService.Current.ThemeChanged` (raise PropertyChanged / invalidate
   visual). Static `readonly` brushes cached at type load must become per-theme lookups.
6. **Verify** after every view: build, then `tests/themetest` (through the build lock). Midnight must stay ≤ 0.1 %
   per page against `E:\cluadex-bld\themes\golden\`; look at `renders\<theme>\_contact.png` and any `diff\*.png`.

```
powershell -NoProfile -ExecutionPolicy Bypass -File E:\cluadex-bld\locked.ps1 E:\Code\ClaudeClient dotnet build CluadeX/CluadeX.csproj -p:OutDir=E:\cluadex-bld\<team>\bin\
powershell -NoProfile -ExecutionPolicy Bypass -File E:\cluadex-bld\locked.ps1 E:\Code\ClaudeClient\tests\themetest dotnet run -p:CluadeXBin=E:\cluadex-bld\<team>\bin\
```

## Who converts what

| Team | Files (hard-coded colours + StaticResource → DynamicResource) |
|---|---|
| **Foundation (done)** | `App.xaml`, `Resources/Theme.xaml`, `MainWindow.xaml` (fully on tokens), `MainWindow.xaml.cs`, `Views/Settings/ThemePicker.xaml` |
| **A** — main surfaces | `Views/ChatView.xaml`(+`.cs`), `Views/CodeEditorView.xaml`(+`.cs`), `Views/Editor/*.cs` (AgentGlowRenderer, BreakpointMargin, ChoiceDialog, CompletionData, MatchHighlight, MinimapControl, NeonHighlighting, SafeShell) |
| **B** — settings & managers | `Views/SettingsView.xaml`, `FeaturesView`, `ModelManagerView`, `McpServersView`, `PluginManagerView`, `SkillsView`, `SubAgentsView`, `HookLibraryView`, `InstinctsView`, `PermissionsView`, `SecurityShieldView` (`.xaml` + `.xaml.cs`) |
| **C** — the rest + C# | `Views/TaskManagerView`, `TimeMachineView`, `HexEditorView`, `DebugLogView`, `LicenseWindow`, `BrainXInviteWindow`, `DgxSparkWindow`, `Views/Preview/*` (PreviewPane, PreviewWindow, PreviewDock.cs, …) and every C# colour construction in `Converters/Converters.cs`, `ViewModels/*` (MainViewModel GPU temp brush, CommandPalette KindColor, CodeEditorViewModel …), `Services/*` (Helpers/InputDialog, CodeWorkspaceService), `Models/*` (CodeEditorModels, LogEntry, SecurityRule, SubAgentDefinition, BuddyModels, Instinct), `Helpers/MarkdownHelper.cs` |

(`Services/Browser/PixelDigest.cs` measures pixels — not a UI colour, leave it.)

## Token catalogue (Midnight values)

| Token | Kind | Midnight value |
|---|---|---|
| **Fonts** | | |
| `Font.Ui` | font | Segoe UI, Arial |
| `Font.Ui.Display` | font | Space Grotesk, Segoe UI, Arial |
| `Font.Mono` | font | Cascadia Code, Consolas, Courier New |
| `Font.Mono.Display` | font | JetBrains Mono, Cascadia Code, Consolas |
| **Backgrounds: window → panel → elevated** | | |
| `Bg.Window` | brush | `#06071A` |
| `Bg.Panel` | brush | `#0A0C22` |
| `Bg.Elevated` | brush | `#0F1230` |
| `Bg.Panel.Solid` | brush | `#11152E` |
| `Bg.Overlay` | brush | `#06071A` @ 0.85 |
| `Bg.Scrim` | brush | `#B4000000` |
| **The app-wide backdrop behind every page (Midnight: a violet nebula + four blurred orbs)** | | |
| `Backdrop.Nebula` | radial | radial `#22A672FF` → `#06071A` |
| `Backdrop.Orb.1` | radial | radial `#A672FF` → `Transparent` |
| `Backdrop.Orb.2` | radial | radial `#4CDFFF` → `Transparent` |
| `Backdrop.Orb.3` | radial | radial `#FF5EC4` → `Transparent` |
| `Backdrop.Orb.4` | radial | radial `#5CFFB0` → `Transparent` |
| **Surfaces: panels and cards on top of the backgrounds** | | |
| `Surface.Panel` | gradient | linear `#C8141838` → `#BC0C0E26` |
| `Surface.Card` | gradient | linear `#9E1C2044` → `#9E0E122C` |
| `Surface.Card.Flat` | brush | `#88141838` |
| `Surface.Glass` | brush | `#7028326E` |
| `Surface.Sunken` | brush | `#1A11152E` |
| `Surface.Raised` | brush | `#2828326E` |
| `Surface.Raised.Strong` | brush | `#3828326E` |
| `Surface.Input` | brush | `#7011152E` |
| `Surface.Chip` | brush | `#3311152E` |
| `Surface.Popup` | brush | `#F00A0C22` |
| `Surface.Badge` | brush | `#22FFFFFF` |
| **Text: brightest → faintest, plus text on dark-on-bright and bright-on-accent** | | |
| `Text.Primary` | brush | `#E8ECFF` |
| `Text.Body` | brush | `#D8DCF5` |
| `Text.Secondary` | brush | `#C0C6EE` |
| `Text.Tertiary` | brush | `#A0A6CC` |
| `Text.Muted` | brush | `#8388BD` |
| `Text.Faint` | brush | `#555A8C` |
| `Text.Inverse` | brush | `#1E1E2E` |
| `Text.OnAccent` | brush | `#FFFFFF` |
| `Text.Link` | brush | `#4CDFFF` |
| **The softer text ladder of the dialogs (License, BrainX, DGX Spark) and the shared controls** | | |
| `Text.Soft` | brush | `#CDD6F4` |
| `Text.Soft.Secondary` | brush | `#A6ADC8` |
| `Text.Soft.Muted` | brush | `#6C7086` |
| **Borders + focus ring** | | |
| `Border.Subtle` | brush | `#8CA0F0` @ 0.08 |
| `Border.Default` | brush | `#A0B4FF` @ 0.18 |
| `Border.Strong` | brush | `#28FFFFFF` |
| `Focus.Ring` | brush | `#4CDFFF` |
| **Accents 1-3 (cyan / violet / magenta) + soft pink, each with a tint (fill) and a border** | | |
| `Accent.1` | brush | `#4CDFFF` |
| `Accent.1.Tint` | brush | `#224CDFFF` |
| `Accent.1.Border` | brush | `#664CDFFF` |
| `Accent.1.Tint.Strong` | brush | `#384CDFFF` |
| `Accent.2` | brush | `#A672FF` |
| `Accent.2.Tint` | brush | `#22A672FF` |
| `Accent.2.Border` | brush | `#55A672FF` |
| `Accent.2.Tint.Strong` | brush | `#38A672FF` |
| `Accent.3` | brush | `#FF5EC4` |
| `Accent.3.Tint` | brush | `#22FF5EC4` |
| `Accent.3.Border` | brush | `#66FF5EC4` |
| `Accent.4` | brush | `#FF8AA6` |
| **60 % accent fills for glowing strokes and halos** | | |
| `Accent.1.Glow` | brush | `#4CDFFF` @ 0.6 |
| `Accent.2.Glow` | brush | `#A672FF` @ 0.6 |
| `Accent.3.Glow` | brush | `#FF5EC4` @ 0.6 |
| `Status.Success.Glow` | brush | `#5CFFB0` @ 0.6 |
| `Accent.Gradient` | gradient | linear `#FF5CA8` → `#A672FF` |
| **Brand colours that stay recognisable in every theme: CluadeX Pro (license) pink, NVIDIA DGX Spark green** | | |
| `Brand.Pro` | brush | `#FF5CA8` |
| `Brand.Pro.Border` | brush | `#AAFF5CA8` |
| `Brand.Spark` | brush | `#76D16B` |
| `Brand.Spark.Border` | brush | `#AA76D16B` |
| **Status** | | |
| `Status.Success` | brush | `#5CFFB0` |
| `Status.Success.Tint` | brush | `#225CFFB0` |
| `Status.Success.Border` | brush | `#665CFFB0` |
| `Status.Success.Tint.Strong` | brush | `#445CFFB0` |
| `Status.Success.Soft` | brush | `#A6E3A1` |
| `Status.Warning.Soft` | brush | `#F9E2AF` |
| `Status.Caution.Soft` | brush | `#FAB387` |
| `Status.Danger.Soft` | brush | `#F38BA8` |
| `Accent.2.Soft` | brush | `#CBA6F7` |
| `Status.Warning` | brush | `#FFD166` |
| `Status.Warning.Tint` | brush | `#22FFD166` |
| `Status.Warning.Border` | brush | `#66FFD166` |
| `Status.Danger` | brush | `#FF6E6E` |
| `Status.Danger.Tint` | brush | `#22FF6E6E` |
| `Status.Danger.Border` | brush | `#66FF6E6E` |
| `Status.Info` | brush | `#5A8DFF` |
| `Status.Info.Tint` | brush | `#225A8DFF` |
| `Status.Info.Border` | brush | `#665A8DFF` |
| **Interaction states (overlays on whatever is underneath)** | | |
| `State.Hover` | brush | `#1AFFFFFF` |
| `State.Hover.Subtle` | brush | `#15FFFFFF` |
| `State.Hover.Strong` | brush | `#22FFFFFF` |
| `State.Pressed` | brush | `#33FFFFFF` |
| `State.Selected` | brush | `#3328326E` |
| `State.Selected.Strong` | brush | `#B22E3870` |
| `State.Fill.Faint` | brush | `#11FFFFFF` |
| `Selection.Text` | brush | `#59A672FF` |
| **Title bar (42 px): background, the CluadeX caption, the min/max/close buttons, the Ctrl+K pill** | | |
| `TitleBar.Background` | gradient | linear `#EB141838` → `#D90A0C22` |
| `TitleBar.Border` | brush | `#8CA0F0` @ 0.08 |
| `TitleBar.Foreground` | brush | `#C0C6EE` |
| `TitleBar.Chip.Background` | brush | `#3311152E` |
| `TitleBar.Button.Foreground` | brush | `#8388BD` |
| `TitleBar.Button.Hover.Background` | brush | `#22FFFFFF` |
| `TitleBar.Button.Hover.Foreground` | brush | `#E8ECFF` |
| `TitleBar.Close.Hover.Background` | brush | `#FF6E6E` |
| `TitleBar.Close.Hover.Foreground` | brush | `White` |
| `TitleBar.Search.Background` | brush | `#1AFFFFFF` |
| `TitleBar.Search.Border` | brush | `#8CA0F0` @ 0.08 |
| `TitleBar.Search.Foreground` | brush | `#8388BD` |
| `TitleBar.Search.Hover.Border` | brush | `#4CDFFF` |
| `TitleBar.Search.Hover.Foreground` | brush | `#E8ECFF` |
| `TitleBar.Search.Kbd.Background` | brush | `#22FFFFFF` |
| **Title-bar chips: DGX Spark (NVIDIA green), license, buddy** | | |
| `Spark.Chip.Background` | brush | `#1476D16B` |
| `Spark.Chip.Hover.Background` | brush | `#3376D16B` |
| `Spark.Chip.Border` | brush | `#9976D16B` |
| `Spark.Chip.Dot` | brush | `#5576D16B` |
| `Spark.Chip.Dot.Active` | brush | `#9BE26B` |
| `Spark.Chip.Foreground` | brush | `#9BE26B` |
| `Glow.Spark.Active` | effect | shadow `#76D16B` blur 12 depth 0 opacity 0.7 |
| `License.Chip.Foreground` | brush | `White` |
| `License.Chip.Border` | brush | `#99FFFFFF` |
| `License.Chip.Background` | gradient | linear `#FF5CA8` → `#A672FF` |
| `Glow.License.Chip` | effect | shadow `#FF5CA8` blur 14 depth 0 opacity 0.75 |
| `License.Chip.Trial.Foreground` | brush | `#1E1E2E` |
| `License.Chip.Trial.Background` | gradient | linear `#FFD166` → `#FFA94D` |
| `Glow.License.Trial` | effect | shadow `#FFD166` blur 12 depth 0 opacity 0.6 |
| `License.Chip.Active.Foreground` | brush | `#5CFFB0` |
| `License.Chip.Active.Border` | brush | `#665CFFB0` |
| `License.Chip.Active.Background` | brush | `#1F5CFFB0` |
| `Buddy.Chip.Background` | brush | `#7028326E` |
| `Buddy.Chip.Border` | brush | `#665CFFB0` |
| `Glow.Buddy.Chip` | effect | shadow `#5CFFB0` blur 14 depth 0 opacity 0.4 |
| `Buddy.Avatar` | radial | radial `#5CFFB0` → `#1F8C5C` |
| **Activity bar (60 px rail on the left)** | | |
| `ActivityBar.Background` | gradient | linear `#D90C0E26` → `#D906071A` |
| `ActivityBar.Border` | brush | `#8CA0F0` @ 0.08 |
| `ActivityBar.Divider` | brush | `#8CA0F0` @ 0.08 |
| `ActivityBar.Foreground` | brush | `#8388BD` |
| `ActivityBar.Hover.Background` | brush | `#1AFFFFFF` |
| `ActivityBar.Hover.Foreground` | brush | `#4CDFFF` |
| `ActivityBar.Checked.Background` | radial | radial `#52A672FF` → `#001E2056` |
| `ActivityBar.Checked.Border` | brush | `#80A672FF` |
| `ActivityBar.Checked.Foreground` | brush | `#FFFFFFFF` |
| `ActivityBar.Indicator` | brush | `#4CDFFF` |
| `Glow.ActivityBar.Indicator` | effect | shadow `#4CDFFF` blur 14 depth 0 opacity 0.9 |
| `Glow.ActivityBar.Checked` | effect | shadow `#A672FF` blur 22 depth 0 opacity 0.55 |
| `ActivityBar.Logo.Background` | brush | `#0F1230` |
| `ActivityBar.Logo.Halo` | radial | radial `#A672FF` → `Transparent` |
| `ActivityBar.Logo.Divider` | brush | `#A0B4FF` @ 0.18 |
| **Sidebar (chat history panel next to the activity bar)** | | |
| `Sidebar.Background` | brush | `#A60A0C22` |
| `Sidebar.Border` | brush | `#8CA0F0` @ 0.08 |
| `Sidebar.Toggle.Checked` | brush | `#FF5EC4` |
| **Status bar (34 px) + the LED context meter** | | |
| `StatusBar.Background` | gradient | linear `#EB0C0E26` → `#F206071A` |
| `StatusBar.Border` | brush | `#8CA0F0` @ 0.08 |
| `StatusBar.Foreground` | brush | `#8388BD` |
| `StatusBar.Chip.Background` | gradient | linear `#66FF5EC4` → `#66A672FF` |
| `StatusBar.Chip.Border` | brush | `#FF5EC4` |
| `StatusBar.Chip.Foreground` | brush | `White` |
| `Glow.StatusBar.Chip` | effect | shadow `#FF5EC4` blur 18 depth 0 opacity 0.7 |
| `StatusBar.Brain.Background` | brush | `#1AA672FF` |
| `StatusBar.Brain.Border` | brush | `#55A672FF` |
| `StatusBar.Brain.Connected` | brush | `#A672FF` |
| `Glow.StatusBar.Pip` | effect | shadow `#5CFFB0` blur 10 depth 0 opacity 0.8 |
| `Led.Off` | brush | `#15FFFFFF` |
| `Glow.Led.Success` | effect | shadow `#5CFFB0` blur 8 depth 0 opacity 0.8 |
| `Glow.Led.Accent` | effect | shadow `#4CDFFF` blur 8 depth 0 opacity 0.8 |
| `Glow.Led.Warning` | effect | shadow `#FFD166` blur 8 depth 0 opacity 0.8 |
| `Glow.Led.Caution` | effect | shadow `#FF8AA6` blur 8 depth 0 opacity 0.8 |
| `Glow.Led.Danger` | effect | shadow `#FF6E6E` blur 8 depth 0 opacity 0.8 |
| **Charts + sparklines (the status-bar GPU sparkline, usage meters)** | | |
| `Chart.Line` | brush | `#A672FF` |
| `Chart.Background` | brush | `#221E2056` |
| `Chart.Grid` | brush | `#8CA0F0` @ 0.08 |
| `Glow.Chart.Line` | effect | shadow `#A672FF` blur 6 depth 0 opacity 0.7 |
| `Chart.Series.1` | brush | `#4CDFFF` |
| `Chart.Series.2` | brush | `#A672FF` |
| `Chart.Series.3` | brush | `#FF5EC4` |
| `Chart.Series.4` | brush | `#5CFFB0` |
| `Chart.Series.5` | brush | `#FFD166` |
| **Update banner, model-loading overlay, command palette** | | |
| `Banner.Background` | gradient | linear `#1A6366F1` → `#1A06B6D4` |
| `Banner.Button.Background` | gradient | linear `#992C5A` → `#5A2A99` |
| `Glow.Loading.Card` | effect | shadow `#A672FF` blur 40 depth 0 opacity 0.5 |
| `Loading.Orb` | radial | radial `#A672FF` → `#4CDFFF` |
| `Palette.Background` | brush | `#F00A0C22` |
| `Shadow.Palette` | effect | shadow `Black` blur 34 depth 0 opacity 0.6 |
| **Nav list buttons (NavButton style)** | | |
| `Nav.Foreground` | brush | `#A6ADC8` |
| `Nav.Indicator` | gradient | linear `#89B4FA` → `#B4BEFE` → `#CBA6F7` |
| `Nav.IconBubble` | gradient | linear `#89B4FA` → `#CBA6F7` |
| `Nav.Chevron` | brush | `#6C7086` |
| `Nav.Hover.Background` | gradient | linear `#2A2A40` → `#30304A` → `#2F2E48` |
| `Nav.Hover.Border` | brush | `#20FFFFFF` |
| `Nav.Hover.Icon` | brush | `#89B4FA` |
| `Nav.Hover.Foreground` | brush | `#CDD6F4` |
| `Nav.Selected.Background` | gradient | linear `#2E3A5C` → `#33305A` → `#3C2A5A` |
| `Nav.Selected.Border` | brush | `#3089B4FA` |
| `Nav.Selected.Icon` | brush | `#E0CBA6F7` |
| `Nav.Selected.Chevron` | brush | `#CBA6F7` |
| `Nav.Selected.Foreground` | brush | `#F5E8FF` |
| `Glow.Nav.Selected` | effect | shadow `#89B4FA` blur 14 depth 0 opacity 0.35 |
| **Buttons (PrimaryButton / SecondaryButton / DangerButton / IconButton styles)** | | |
| `Button.Primary.Background` | gradient | linear `#A3C5FF` → `#89B4FA` → `#6E9AE8` → `#5A87D8` |
| `Button.Primary.Hover.Background` | gradient | linear `#C5D6FF` → `#B4BEFE` → `#89A8F0` → `#7A99E8` |
| `Button.Primary.Foreground` | brush | `#11111B` |
| `Button.Primary.Border` | brush | `#55FFFFFF` |
| `Button.Primary.Pressed.Border` | brush | `#33FFFFFF` |
| `Button.Primary.Highlight` | brush | `#30FFFFFF` |
| `Button.Secondary.Background` | gradient | linear `#42425A` → `#353548` → `#2E2E40` |
| `Button.Secondary.Hover.Background` | gradient | linear `#545470` → `#454560` → `#3A3A50` |
| `Button.Secondary.Foreground` | brush | `#CDD6F4` |
| `Button.Secondary.Border` | brush | `#18FFFFFF` |
| `Button.Secondary.Highlight` | brush | `#15FFFFFF` |
| `Button.Danger.Foreground` | brush | `#F38BA8` |
| `Button.Icon.Foreground` | brush | `#A6ADC8` |
| `Button.Icon.Hover.Background` | gradient | linear `#3A3A50` → `#2D2D42` → `#282838` |
| `Button.Icon.Hover.Border` | brush | `#15FFFFFF` |
| `Button.Icon.Hover.Foreground` | brush | `#CDD6F4` |
| `Caption.Foreground` | brush | `#A6ADC8` |
| `Caption.Hover.Background` | gradient | linear `#4E4E68` → `#3E3E55` → `#353548` |
| `Caption.Hover.Foreground` | brush | `#CDD6F4` |
| `Caption.Pressed.Background` | brush | `#585B70` |
| `Caption.Close.Hover.Background` | brush | `#F38BA8` |
| `Caption.Close.Pressed.Background` | brush | `#CC3333` |
| **Inputs (ModernTextBox / GlowTextBox), dropdowns, checkboxes, sliders, progress bars** | | |
| `Input.Background` | brush | `#313244` |
| `Input.Foreground` | brush | `#CDD6F4` |
| `Input.Caret` | brush | `#CDD6F4` |
| `Input.Border` | brush | `#45475A` |
| `Input.Focus.Border` | brush | `#89B4FA` |
| `Input.InnerShadow` | brush | `#08000000` |
| `Dropdown.Background` | gradient | linear `#42425A` → `#353548` → `#2E2E40` |
| `Dropdown.Foreground` | brush | `#CDD6F4` |
| `Dropdown.Border` | brush | `#18FFFFFF` |
| `Dropdown.Hover.Border` | brush | `#28FFFFFF` |
| `Dropdown.Open.Border` | brush | `#89B4FA` |
| `Dropdown.Arrow` | brush | `#A6ADC8` |
| `Dropdown.Highlight` | brush | `#12FFFFFF` |
| `Dropdown.Item.Foreground` | brush | `#CDD6F4` |
| `Dropdown.Item.Hover.Background` | gradient | linear `#4E4E68` → `#3E3E55` → `#353548` |
| `Dropdown.Item.Selected.Foreground` | brush | `#89B4FA` |
| `Popup.Background` | gradient | linear `#383850` → `#313244` → `#2C2C3E` → `#272738` |
| `Popup.Border` | brush | `#20FFFFFF` |
| `CheckBox.Foreground` | brush | `#CDD6F4` |
| `CheckBox.Background` | gradient | linear `#42425A` → `#353548` → `#2E2E40` |
| `CheckBox.Border` | brush | `#18FFFFFF` |
| `CheckBox.Hover.Border` | brush | `#30FFFFFF` |
| `CheckBox.Checked.Background` | gradient | linear `#A3C5FF` → `#89B4FA` → `#6E9AE8` → `#5A87D8` |
| `CheckBox.Checked.Border` | brush | `#44FFFFFF` |
| `CheckBox.Mark` | brush | `#11111B` |
| `Slider.Background` | brush | `#313244` |
| `Slider.Foreground` | brush | `#89B4FA` |
| `Slider.Track` | gradient | linear `#42425A` → `#353548` → `#2E2E40` |
| `Slider.Fill` | gradient | linear `#A3C5FF` → `#89B4FA` → `#6E9AE8` → `#5A87D8` |
| `Slider.Thumb` | gradient | linear `#A3C5FF` → `#89B4FA` → `#6E9AE8` → `#5A87D8` |
| `Slider.Thumb.Hover` | gradient | linear `#C5D6FF` → `#B4BEFE` → `#89A8F0` → `#7A99E8` |
| `Slider.Thumb.Border` | brush | `#44FFFFFF` |
| `Slider.Thumb.Gloss` | radial | radial `#40FFFFFF` → `Transparent` |
| `Progress.Track` | gradient | linear `#42425A` → `#353548` → `#2E2E40` |
| `Progress.Fill` | gradient | linear `#A3C5FF` → `#89B4FA` → `#6E9AE8` → `#5A87D8` |
| `Progress.Border` | brush | `#10000000` |
| `Progress.Context.Fill` | brush | `#94E2D5` |
| **Cards, tooltips, scrollbars, expanders, list rows, badges, code blocks** | | |
| `Card.Background` | gradient | linear `#383850` → `#313244` → `#2C2C3E` → `#272738` |
| `Card.Border` | brush | `#12FFFFFF` |
| `Tooltip.Background` | gradient | linear `#4E4E68` → `#3E3E55` → `#353548` |
| `Tooltip.Foreground` | brush | `#CDD6F4` |
| `Tooltip.Border` | brush | `#20FFFFFF` |
| `Scrollbar.Thumb` | brush | `#585B70` |
| `Expander.Foreground` | brush | `#A6ADC8` |
| `Expander.Arrow` | brush | `#6C7086` |
| `Expander.Hover.Background` | gradient | linear `#3A3A50` → `#2D2D42` → `#282838` |
| `ListItem.Foreground` | brush | `#A6ADC8` |
| `ListItem.Hover.Background` | gradient | linear `#3A3A50` → `#2D2D42` → `#282838` |
| `ListItem.Hover.Border` | brush | `#10FFFFFF` |
| `ListItem.Hover.Foreground` | brush | `#CDD6F4` |
| `ListItem.Pressed.Background` | gradient | linear `#4E4E68` → `#3E3E55` → `#353548` |
| `Badge.Background` | gradient | linear `#4E4E68` → `#3E3E55` → `#353548` |
| `Badge.Border` | brush | `#10FFFFFF` |
| `CodeBlock.Background` | gradient | linear `#151520` → `#11111B` → `#0E0E16` |
| `CodeBlock.Border` | brush | `#15FFFFFF` |
| **Code editor (AvalonEdit + minimap + breakpoint gutter) and syntax colours** | | |
| `Editor.Background` | brush | `Transparent` |
| `Editor.Foreground` | brush | `#E8ECFF` |
| `Editor.LineNumber` | brush | `#555A8C` |
| `Editor.Caret` | brush | `#4CDFFF` |
| `Editor.Selection` | brush | `#59A672FF` |
| `Editor.Gutter` | brush | `#3011152E` |
| `Editor.Minimap.Background` | brush | `#7011152E` |
| `Editor.Minimap.Bar` | brush | `#829AA5E8` |
| `Editor.Minimap.Comment` | brush | `#407C85A0` |
| `Editor.Minimap.Viewport` | brush | `#1EFFFFFF` |
| `Editor.Minimap.ViewportBorder` | brush | `#994CDFFF` |
| `Editor.Minimap.Hover` | brush | `#14FFFFFF` |
| `Editor.Minimap.Caret` | brush | `#CCFFD37E` |
| `Editor.Minimap.Change` | brush | `#E05CFFB0` |
| `Editor.Breakpoint` | brush | `#FF5370` |
| `Editor.Breakpoint.Halo` | brush | `#55FF5370` |
| `Editor.ExecutionLine` | brush | `#FFD37E` |
| `Editor.ExecutionLine.Fill` | brush | `#38FFD37E` |
| `Editor.ExecutionLine.Border` | brush | `#80FFD37E` |
| `Editor.AgentGlow` | brush | `#A672FF` |
| `Editor.Match` | brush | `#66FFC54C` |
| `Editor.Match.Foreground` | brush | `#FFF4D6` |
| `Syntax.Keyword` | brush | `#A672FF` |
| `Syntax.Type` | brush | `#4CDFFF` |
| `Syntax.String` | brush | `#5CFFB0` |
| `Syntax.Number` | brush | `#FFC66D` |
| `Syntax.Preprocessor` | brush | `#FF5EC4` |
| `Syntax.Comment` | brush | `#7E8AA8` |
| **Shadows and glows** | | |
| `Shadow.Card` | effect | shadow `#0A0A14` blur 16 depth 4 opacity 0.6 |
| `Shadow.Button` | effect | shadow `#0A0A14` blur 8 depth 2 opacity 0.5 |
| `Shadow.Popup` | effect | shadow `#08080F` blur 24 depth 8 opacity 0.7 |
| `Shadow.Panel` | effect | shadow `Black` blur 40 depth 0 opacity 0.6 |
| `Glow.Accent.1` | effect | shadow `#4CDFFF` blur 22 depth 0 opacity 0.6 |
| `Glow.Accent.2` | effect | shadow `#A672FF` blur 22 depth 0 opacity 0.6 |
| `Glow.Accent.3` | effect | shadow `#FF5EC4` blur 22 depth 0 opacity 0.6 |
| `Glow.Success` | effect | shadow `#5CFFB0` blur 22 depth 0 opacity 0.6 |

## Legacy keys → tokens

### Neon palette (used everywhere, never overridden locally)

| Legacy key | Token | Match |
|---|---|---|
| `NeonBg0` / `NeonBg0Brush` | `Bg.Window` | exact |
| `NeonBg1` / `NeonBg1Brush` | `Bg.Panel` | exact |
| `NeonBg2` / `NeonBg2Brush` | `Bg.Elevated` | exact |
| `NeonPanelSolid` / `NeonPanelSolidBrush` | `Bg.Panel.Solid` | exact |
| `NeonInk0` / `NeonInk0Brush` | `Text.Primary` | exact |
| `NeonInk1` / `NeonInk1Brush` | `Text.Secondary` | exact |
| `NeonInk2` / `NeonInk2Brush` | `Text.Muted` | exact |
| `NeonInk3` / `NeonInk3Brush` | `Text.Faint` | exact |
| `NeonCyan` / `NeonCyanBrush` | `Accent.1` (`Text.Link` for links, `Focus.Ring` for focus) | exact |
| `NeonViolet` / `NeonVioletBrush` | `Accent.2` | exact |
| `NeonMagenta` / `NeonMagentaBrush` | `Accent.3` | exact |
| `NeonPink` / `NeonPinkBrush` | `Accent.4` | exact |
| `NeonBlue` / `NeonBlueBrush` | `Status.Info` | exact |
| `NeonMint` / `NeonMintBrush` | `Status.Success` | exact |
| `NeonAmber` / `NeonAmberBrush` | `Status.Warning` | exact |
| `NeonRed` / `NeonRedBrush` | `Status.Danger` | exact |
| `NeonCyanGlowBrush` · `NeonVioletGlowBrush` · `NeonMagentaGlowBrush` · `NeonMintGlowBrush` | `Accent.1.Glow` · `Accent.2.Glow` · `Accent.3.Glow` · `Status.Success.Glow` | exact |
| `NeonLineBrush` | `Border.Subtle` | exact |
| `NeonLineHiBrush` | `Border.Default` | exact |
| `NeonPanelFill` | `Surface.Panel` | exact |
| `NeonCardFill` | `Surface.Card` | exact |
| `NeonRewindGradient` | `Banner.Button.Background` | exact |
| `NeonRewindHover`, `NeonSpineGradient`, `NeonAvatarAi`, `NeonAvatarHuman` | (Time Machine / chat avatars) — add `TimeMachine.*` / `Avatar.*` tokens when converting | — |
| `NeonGlowCyan` · `NeonGlowViolet` · `NeonGlowMagenta` · `NeonGlowMint` | `Glow.Accent.1` · `Glow.Accent.2` · `Glow.Accent.3` · `Glow.Success` | exact |
| `NeonPanelShadow` | `Shadow.Panel` | exact |
| `NeonUiFont` · `NeonMonoFont` | `Font.Ui.Display` · `Font.Mono.Display` | exact |

### Catppuccin keys — depends on WHERE they are used

Inside the seven views with a local override block the key shows its **local** (neon) value; elsewhere (and in the
shared styles) it shows the app-level Catppuccin value.

| Legacy key | In the 7 views with a local block | Elsewhere (License / BrainX / DGX Spark dialogs) |
|---|---|---|
| `CrustBrush` | `Bg.Window` (#06071A) | `Button.Primary.Foreground` / `CheckBox.Mark` (#11111B) |
| `MantleBrush` | `Bg.Panel` (#0A0C22) | — (unused) |
| `BaseBrush` | `Bg.Elevated` (#0F1230) | `Text.Inverse` (#1E1E2E) |
| `Surface0Brush` | `Surface.Sunken` (#1A11152E) | `Input.Background` (#313244) |
| `Surface1Brush` | `Surface.Raised` (#2828326E) | `Input.Border` (#45475A) |
| `Surface2Brush` | `Surface.Raised.Strong` (#3828326E) | `Scrollbar.Thumb` (#585B70) |
| `Overlay0Brush` | `Text.Muted` (#8388BD) | `Text.Soft.Muted` (#6C7086) |
| `Overlay1Brush` | `Text.Tertiary` (#A0A6CC) | — |
| `Subtext0Brush` | `Text.Secondary` (#C0C6EE) | `Text.Soft.Secondary` (#A6ADC8) |
| `Subtext1Brush` | `Text.Body` (#D8DCF5) | — |
| `TextBrush` | `Text.Primary` (#E8ECFF) | `Text.Soft` (#CDD6F4) |
| `BlueBrush`, `SapphireBrush`, `SkyBrush`, `TealBrush` | `Accent.1` (#4CDFFF) | Blue → `Input.Focus.Border` (#89B4FA); Teal → `Progress.Context.Fill` |
| `LavenderBrush`, `MauveBrush` | `Accent.2` (#A672FF) | Mauve → `Accent.2.Soft` (#CBA6F7) |
| `GreenBrush` | `Status.Success` (#5CFFB0) | `Status.Success.Soft` (#A6E3A1) |
| `YellowBrush`, `PeachBrush`, `RosewaterBrush` | `Status.Warning` (#FFD166) | Yellow → `Status.Warning.Soft` (#F9E2AF) |
| `RedBrush` | `Status.Danger` (#FF6E6E) | `Status.Danger.Soft` / `Button.Danger.Foreground` (#F38BA8) |
| `PinkBrush`, `MaroonBrush`, `FlamBrush` | `Accent.4` (#FF8AA6) | — |
| `MainFont` · `MonoFont` | `Font.Ui.Display` · `Font.Mono.Display` (local override) | `Font.Ui` · `Font.Mono` |
| `*Color` keys (`TextColor`, `BlueColor` …) | as their brush | as their brush (`ThemeService.ColorOf`) |

### Metal / nav gradients (only the shared styles used them — converted)

| Legacy key | Token(s) |
|---|---|
| `MetalBlueButton` / `MetalBlueHover` | `Button.Primary.Background` / `.Hover.Background`, `CheckBox.Checked.Background`, `Slider.Fill`, `Slider.Thumb`(`.Hover`), `Progress.Fill` |
| `MetalSecondary` / `MetalSecondaryHover` | `Button.Secondary.Background` / `.Hover.Background`, `Dropdown.Background`, `CheckBox.Background`, `Slider.Track`, `Progress.Track` |
| `MetalSurface0` | `Button.Icon.Hover.Background`, `Expander.Hover.Background`, `ListItem.Hover.Background` |
| `MetalSurface1` | `Dropdown.Item.Hover.Background`, `Tooltip.Background`, `Caption.Hover.Background`, `ListItem.Pressed.Background`, `Badge.Background` |
| `MetalCard` | `Card.Background`, `Popup.Background` |
| `NavAccentGlow` · `NavCheckedFill` · `NavHoverFill` · `NavIconBubble` | `Nav.Indicator` · `Nav.Selected.Background` · `Nav.Hover.Background` · `Nav.IconBubble` |
| `MetalSidebar`, `MenuHeaderFill`, `AccentGradient`, `TealGradient`, `WarmGradient` | unused today — map when met |
| `CardShadow` · `ButtonShadow` · `PopupShadow` | `Shadow.Card` · `Shadow.Button` · `Shadow.Popup` |

## Hard-coded colours in the views → tokens

Every distinct literal colour in `Views/**/*.xaml` (MainWindow is done), most-used first. `exact` = same pixels;
`≈ ΔN` = within N/255 per channel over the dark panel background (≤ 8 passes the golden check); **add a token** =
nothing close — add one (all three theme files) with this exact value, or accept the pixel change on purpose.

| Hard-coded value | Uses | Token | Match |
|---|---:|---|---|
| `#FF4CDFFF` | 56 | `Accent.1` | exact |
| `#FFA672FF` | 41 | `Accent.2` | exact |
| `#22FFFFFF` | 36 | `Surface.Badge` | exact |
| `#FFFF5EC4` | 27 | `Accent.3` | exact |
| `#FFFFD166` | 25 | `Status.Warning` | exact |
| `#FFFF8AA6` | 24 | `Accent.4` | exact |
| `#7011152E` | 21 | `Surface.Input` | exact |
| `#FF5CFFB0` | 20 | `Status.Success` | exact |
| `#FF06071A` | 19 | `Bg.Window` | exact |
| `#3328326E` | 15 | `State.Selected` | exact |
| `#225CFFB0` | 12 | `Status.Success.Tint` | exact |
| `#FFFF6E6E` | 12 | `Status.Danger` | exact |
| `#7028326E` | 11 | `Surface.Glass` | exact |
| `#FF8388BD` | 11 | `Text.Muted` | exact |
| `#11FFFFFF` | 9 | `State.Fill.Faint` | exact |
| `#22FFD166` | 9 | `Status.Warning.Tint` | exact |
| `#3311132E` | 8 | `Surface.Sunken` | ≈ Δ1 |
| `#7F141838` | 8 | `Surface.Card.Flat` | ≈ Δ1 |
| `#88141838` | 8 | `Surface.Card.Flat` | exact |
| `#1A11152E` | 7 | `Surface.Sunken` | exact |
| `#22A672FF` | 7 | `Accent.2.Tint` | exact |
| `#2828326E` | 7 | `Surface.Raised` | exact |
| `#3828326E` | 7 | `Surface.Raised.Strong` | exact |
| `#B22E3870` | 7 | `State.Selected.Strong` | exact |
| `#FF0A0C22` | 7 | `Bg.Panel` | exact |
| `#FF0F1230` | 7 | `Bg.Elevated` | exact |
| `#FFA0A6CC` | 7 | `Text.Tertiary` | exact |
| `#FFC0C6EE` | 7 | `Text.Secondary` | exact |
| `#FFD8DCF5` | 7 | `Text.Body` | exact |
| `#FFE8ECFF` | 7 | `Text.Primary` | exact |
| `#14FFFFFF` | 6 | `Editor.Minimap.Hover` | exact |
| `#15FFFFFF` | 5 | `State.Hover.Subtle` | exact |
| `#1AFFFFFF` | 5 | `State.Hover` | exact |
| `#5528326E` | 5 | `Status.Info.Tint` | ≈ Δ4 |
| `#22000000` | 4 | `Bg.Overlay` | ≈ Δ2 |
| `#FF30344A` | 4 | `Border.Strong` | ≈ Δ5 |
| `#FFFF5CA8` | 4 | `Brand.Pro` | exact |
| `#10FFFFFF` | 3 | `Badge.Border` | exact |
| `#224CDFFF` | 3 | `Accent.1.Tint` | exact |
| `#22FF5EC4` | 3 | `Accent.3.Tint` | exact |
| `#22FF6E6E` | 3 | `Status.Danger.Tint` | exact |
| `#5511152E` | 3 | `Surface.Input` | ≈ Δ1 |
| `#8C0A0C22` | 3 | `Bg.Panel` | exact |
| `#FA0A0C22` | 3 | `Bg.Panel` | exact |
| `#FF60657A` | 3 | `Text.Soft.Muted` | **Δ12 — add a token** |
| `#FFA6E3A1` | 3 | `Status.Success.Soft` | exact |
| `#0A06071A` | 2 | `Bg.Panel` | exact |
| `#144CDFFF` | 2 | `Border.Subtle` | ≈ Δ5 |
| `#1A40A02B` | 2 | `Bg.Panel.Solid` | **Δ11 — add a token** |
| `#1A4CDFFF` | 2 | `Accent.1.Tint` | ≈ Δ6 |
| `#1AF38BA8` | 2 | `Text.Inverse` | ≈ Δ5 |
| `#33000000` | 2 | `Bg.Overlay` | ≈ Δ2 |
| `#3311152E` | 2 | `Surface.Chip` | exact |
| `#332ECC71` | 2 | `Status.Success.Tint` | ≈ Δ6 |
| `#33FFFFFF` | 2 | `State.Pressed` | exact |
| `#384CDFFF` | 2 | `Accent.1.Tint.Strong` | exact |
| `#4011132E` | 2 | `Surface.Chip` | ≈ Δ1 |
| `#445CFFB0` | 2 | `Status.Success.Tint.Strong` | exact |
| `#44FAB387` | 2 | `Editor.ExecutionLine.Fill` | **Δ10 — add a token** |
| `#5576D16B` | 2 | `Spark.Chip.Dot` | exact |
| `#55A672FF` | 2 | `Accent.2.Border` | exact |
| `#665CFFB0` | 2 | `Status.Success.Border` | exact |
| `#80FFFFFF` | 2 | `Syntax.Comment` | **Δ23 — add a token** |
| `#FF1E1E2E` | 2 | `Text.Inverse` | exact |
| `#FF76D16B` | 2 | `Brand.Spark` | exact |
| `#FF89B4FA` | 2 | `Nav.Hover.Icon` | exact |
| `#FF9098B0` | 2 | `Text.Muted` | **Δ16 — add a token** |
| `#FFCDD6F4` | 2 | `Text.Soft` | exact |
| `#FFF38BA8` | 2 | `Status.Danger.Soft` | exact |
| `#FFFF6B6B` | 2 | `Status.Danger` | ≈ Δ3 |
| `#FFFFFFFF` | 2 | `Text.OnAccent` | exact |
| `#0AFFFFFF` | 1 | `Bg.Panel.Solid` | ≈ Δ3 |
| `#1428326E` | 1 | `Surface.Input` | ≈ Δ1 |
| `#1476D16B` | 1 | `Spark.Chip.Background` | exact |
| `#18CBA6F7` | 1 | `State.Fill.Faint` | ≈ Δ5 |
| `#1A5CFFB0` | 1 | `Status.Success.Tint` | ≈ Δ7 |
| `#1A8388BD` | 1 | `Border.Subtle` | ≈ Δ2 |
| `#1AFF6E6E` | 1 | `Status.Danger.Tint` | ≈ Δ8 |
| `#1AFFD166` | 1 | `Text.Inverse` | ≈ Δ5 |
| `#1F5CFFB0` | 1 | `License.Chip.Active.Background` | exact |
| `#20F38BA8` | 1 | `Accent.3.Tint` | ≈ Δ5 |
| `#20F9E2AF` | 1 | `State.Hover` | ≈ Δ5 |
| `#2228326E` | 1 | `Surface.Card.Flat` | ≈ Δ2 |
| `#222D3F66` | 1 | `Bg.Panel.Solid` | ≈ Δ3 |
| `#2276D16B` | 1 | `Editor.Minimap.Hover` | ≈ Δ7 |
| `#22FF6B6B` | 1 | `Status.Danger.Tint` | exact |
| `#245CFFB0` | 1 | `Status.Success.Tint` | ≈ Δ2 |
| `#2628326E` | 1 | `Surface.Card.Flat` | ≈ Δ1 |
| `#26A672FF` | 1 | `Accent.2.Tint` | ≈ Δ4 |
| `#284CDFFF` | 1 | `Accent.1.Tint` | ≈ Δ6 |
| `#2A4CDFFF` | 1 | `Accent.1.Tint` | ≈ Δ7 |
| `#30A672FF` | 1 | `Accent.2.Tint.Strong` | ≈ Δ7 |
| `#30F38BA8` | 1 | `Surface.Badge` | **Δ11 — add a token** |
| `#30F9E2AF` | 1 | `Border.Strong` | ≈ Δ8 |
| `#3328C8FF` | 1 | `Accent.1.Tint.Strong` | ≈ Δ8 |
| `#3376D16B` | 1 | `Spark.Chip.Hover.Background` | exact |
| `#33A672FF` | 1 | `Accent.2.Tint.Strong` | ≈ Δ5 |
| `#33FF4D6D` | 1 | `Accent.3.Tint` | **Δ16 — add a token** |
| `#33FF6E6E` | 1 | `Accent.3.Tint` | **Δ16 — add a token** |
| `#33FFB020` | 1 | `Status.Warning.Tint` | **Δ16 — add a token** |
| `#33FFD166` | 1 | `Editor.ExecutionLine.Fill` | ≈ Δ6 |
| `#3A2ECC71` | 1 | `Status.Success.Tint` | **Δ12 — add a token** |
| `#3AFF4D6D` | 1 | `Accent.3.Tint` | **Δ23 — add a token** |
| `#3CFF5EC4` | 1 | `Border.Strong` | **Δ20 — add a token** |
| `#4011152E` | 1 | `Surface.Chip` | ≈ Δ1 |
| `#4428326E` | 1 | `Surface.Raised.Strong` | ≈ Δ3 |
| `#444CDFFF` | 1 | `Accent.1.Tint.Strong` | **Δ10 — add a token** |
| `#44A672FF` | 1 | `Accent.2.Tint.Strong` | **Δ10 — add a token** |
| `#5528A06E` | 1 | `Status.Success.Tint.Strong` | **Δ16 — add a token** |
| `#558388BD` | 1 | `State.Pressed` | **Δ9 — add a token** |
| `#55FF8A3D` | 1 | `Editor.Breakpoint.Halo` | **Δ18 — add a token** |
| `#664CDFFF` | 1 | `Accent.1.Border` | exact |
| `#66A672FF` | 1 | `Selection.Text` | **Δ11 — add a token** |
| `#66C2185B` | 1 | `Editor.Breakpoint.Halo` | **Δ19 — add a token** |
| `#66FF6E6E` | 1 | `Status.Danger.Border` | exact |
| `#66FFD166` | 1 | `Status.Warning.Border` | exact |
| `#70241436` | 1 | `Bg.Panel.Solid` | ≈ Δ5 |
| `#70301028` | 1 | `Bg.Panel.Solid` | **Δ10 — add a token** |
| `#80141838` | 1 | `Surface.Card.Flat` | ≈ Δ1 |
| `#88FFD166` | 1 | `Editor.ExecutionLine.Border` | **Δ10 — add a token** |
| `#A60A0C22` | 1 | `Sidebar.Background` | exact |
| `#AA76D16B` | 1 | `Brand.Spark.Border` | exact |
| `#AAFF5CA8` | 1 | `Brand.Pro.Border` | exact |
| `#B2060718` | 1 | `Bg.Overlay` | ≈ Δ1 |
| `#D9060718` | 1 | `Bg.Window` | ≈ Δ1 |
| `#FA132A1E` | 1 | `Text.Inverse` | **Δ16 — add a token** |
| `#FA171B42` | 1 | `Status.Info.Tint` | ≈ Δ2 |
| `#FA1D1442` | 1 | `Accent.2.Tint` | ≈ Δ6 |
| `#FF0B1A10` | 1 | `Bg.Panel` | **Δ18 — add a token** |
| `#FF1D6E8C` | 1 | `Accent.1.Border` | **Δ18 — add a token** |
| `#FF286E94` | 1 | `Accent.1.Border` | **Δ26 — add a token** |
| `#FF313244` | 1 | `Input.Background` | exact |
| `#FF3FA34D` | 1 | `Brand.Spark.Border` | **Δ20 — add a token** |
| `#FF45475A` | 1 | `Input.Border` | exact |
| `#FF4F2A8C` | 1 | `Selection.Text` | **Δ29 — add a token** |
| `#FF7E4DCE` | 1 | `Accent.2.Glow` | **Δ39 — add a token** |
| `#FF94E2D5` | 1 | `Progress.Context.Fill` | exact |
| `#FF9BE26B` | 1 | `Spark.Chip.Dot.Active` | exact |
| `#FFC78AFF` | 1 | `Accent.2.Soft` | **Δ28 — add a token** |
| `#FFC9A8FF` | 1 | `Accent.2.Soft` | ≈ Δ8 |
| `#FFFF5370` | 1 | `Editor.Breakpoint` | exact |

## C# colour constructions

| Where | Today | Token |
|---|---|---|
| `Views/CodeEditorView.xaml.cs` caret / selection | `#4CDFFF` / `#59A672FF` | `Editor.Caret` / `Editor.Selection` |
| `Views/Editor/NeonHighlighting.cs` | violet / cyan / mint / `#FFC66D` / magenta / `#7E8AA8` | `Syntax.Keyword` / `.Type` / `.String` / `.Number` / `.Preprocessor` / `.Comment` |
| `Views/Editor/MinimapControl.cs` | bar, comment, viewport, viewport pen, hover, caret, change | `Editor.Minimap.Bar` / `.Comment` / `.Viewport` / `.ViewportBorder` / `.Hover` / `.Caret` / `.Change` |
| `Views/Editor/BreakpointMargin.cs` | breakpoint, halo, execution, gutter | `Editor.Breakpoint` / `.Breakpoint.Halo` / `Editor.ExecutionLine` (`.Fill`, `.Border`) / `Editor.Gutter` |
| `Views/Editor/AgentGlowRenderer.cs` | `#A672FF` | `Editor.AgentGlow` |
| `Views/Editor/MatchHighlight.cs` | `#66FFC54C` / `#FFF4D6` | `Editor.Match` / `Editor.Match.Foreground` |
| `ViewModels/MainViewModel.cs` `GpuTempBrush` | Catppuccin red / peach / yellow / green | `Status.Danger.Soft` / `Status.Caution.Soft` / `Status.Warning.Soft` / `Status.Success.Soft` |
| `ViewModels/MainViewModel.cs` + `CommandPalette.cs` `KindColor` strings | `#4CDFFF` `#A672FF` `#76D16B` … | `Accent.1`, `Accent.2`, `Brand.Spark` … (hand the bound brush, not a hex string) |
| `Converters/Converters.cs` (62) | diff kinds, AI/user avatars, file status, context-usage ramp | `Status.*`, `Accent.*`, `Chart.Series.*` — add `Diff.*` tokens for the diff colours |
| `Models/*` (LogEntry levels, SecurityRule severities, SubAgent colours, Instinct, Buddy rarity, CodeEditorModels git badges) | literal hex | `Status.*` / `Accent.*`; add `Severity.*` / `Git.*` tokens if the meaning needs its own colour |
| `Services/Helpers/InputDialog.cs`, `Views/Editor/ChoiceDialog.cs` | `TryFindResource(legacy key)` | the same call with the token name |
| `MainWindow.xaml.cs` DEV version chip | `#FF6E6E` | done — `SetResourceReference(…, "Status.Danger")` |
