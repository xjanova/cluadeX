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
| `Syntax.Function` | brush | `#ECEF9A` — function / method names, definitions and calls (VS Code `#DCDCAA` family; Classic `#795E26`, Neon Cyber `#FFEE8A`) |
| `Syntax.Control` | brush | `#FFB0F0` — control flow: `if` `elif` `else` `for` `while` `match` `return` `break` `continue` `pass` `yield` `await` (VS Code `#C586C0` family; Classic `#AF00DB`, Neon Cyber `#FFA8EC`) |
| `Syntax.Variable` | brush | `#86B0FF` — members / properties, `$Node` paths, ALL_CAPS constants, shader built-ins, JSON / YAML / TOML / INI keys, shell `$VARS` (VS Code `#9CDCFE` family; Classic `#001080`, Neon Cyber `#8CC2FF`) |
| **Shadows and glows** | | |
| `Shadow.Card` | effect | shadow `#0A0A14` blur 16 depth 4 opacity 0.6 |
| `Shadow.Button` | effect | shadow `#0A0A14` blur 8 depth 2 opacity 0.5 |
| `Shadow.Popup` | effect | shadow `#08080F` blur 24 depth 8 opacity 0.7 |
| `Shadow.Panel` | effect | shadow `Black` blur 40 depth 0 opacity 0.6 |
| `Glow.Accent.1` | effect | shadow `#4CDFFF` blur 22 depth 0 opacity 0.6 |
| `Glow.Accent.2` | effect | shadow `#A672FF` blur 22 depth 0 opacity 0.6 |
| `Glow.Accent.3` | effect | shadow `#FF5EC4` blur 22 depth 0 opacity 0.6 |
| `Glow.Success` | effect | shadow `#5CFFB0` blur 22 depth 0 opacity 0.6 |
| **Chat + code editor (team A): one-offs no shared token matched within 8/255** | | |
| `Accent.1.Tint.Faint` | brush | `#144CDFFF` — compaction marker fill |
| `Accent.1.Border.Soft` | brush | `#444CDFFF` — compaction marker border |
| `Accent.2.Hover` | brush | `#C78AFF` — a solid `Accent.2` button under the mouse ("Compact now") |
| `State.Hover.Danger` | brush | `#33FF6E6E` — hover on a stop / destructive icon button |
| `Banner.Warning.Background` | brush | `#F9E2AF` @ 0.1 — the context-full warning bar |
| `Banner.Danger.Background` | brush | `#70301028` — the merge-conflict banner |
| `Button.Flat.Background` | brush | `#3328326E` — the workbench's small buttons (Save, Save All, Preview rename, Accept …, debug transport) |
| `Button.Flat.Hover.Background` | brush | `#5528326E` |
| `Button.Flat.Foreground` | brush | `#E8ECFF` — text on `Button.Flat.*` and `Button.Success.Background` (paired with the face, so a light face never gets light text) |
| `Button.Success.Background` | brush | `#5528A06E` — Commit |
| `Button.Danger.Solid.Background` | brush | `#66C2185B` — Apply rename (text `Text.OnAccent`) |
| `Chat.Permission.Background` | brush | `#20F9E2AF` — the inline Allow / Deny prompt |
| `Chat.Badge.Warning.Background` | brush | `#30F9E2AF` — retry countdown chip |
| `Chat.Badge.Danger.Background` | brush | `#30F38BA8` — "interrupted" chip |
| `Chat.Diff.Added.Background` · `Chat.Diff.Removed.Background` | brush | `#1A40A02B` · `#1AF38BA8` — edit diff rows in the transcript (text: `Status.Success.Soft` / `Status.Danger.Soft`) |
| `Chat.Diff.Hunk.Foreground` | brush | `#89B4FA` — `@@` hunk header |
| `Editor.Diff.Added.Background` · `Editor.Diff.Removed.Background` | brush | `#3A2ECC71` · `#3AFF4D6D` — side-by-side diff cells (modified rows use the same two, ≈ Δ7) |
| `Completion.Kind.Lsp` · `.Type` · `.Method` · `.Keyword` · `.Other` | brush | `#7EE0A3` · `#8AB8FF` · `#FFD37E` · `#C79AFF` · `#8A8FB0` — kind tag in the completion popup (the popup itself is AvalonEdit's unthemed system-white window) |
| `Syntax.DocComment` | brush | `#808080` — `///` and `<summary>` tags (AvalonEdit's XmlDoc definition, tinted alongside C# / VB) |
| `Tree.Glyph.Fill` · `.Stroke` | brush | `#FFFFFF` · `#818181` — the file tree's expand triangle, collapsed (WPF's own Aero2 values) |
| `Tree.Glyph.Expanded.Fill` · `.Expanded.Stroke` | brush | `#595959` · `#262626` — expanded |
| `Tree.Glyph.Hover.Fill` · `.Hover.Stroke` | brush | `#CCEEFB` · `#27C7F7` — under the mouse |
| `Tree.Glyph.Hover.Expanded.Fill` · `.Hover.Expanded.Stroke` | brush | `#82DFFB` · `#1CC4F7` — expanded, under the mouse |
| `Glow.Toast.Info` · `Glow.Toast.Warning` · `Glow.Toast.Danger` | effect | shadow `#4CDFFF` · `#FFD166` · `#FF6E6E`, blur 18 depth 0 opacity 0.35 · 0.35 · 0.45 — compaction toast |

**Team A notes (ChatView, CodeEditorView, Views/Editor/*).** `Editor.Background` is what the AvalonEdit control, the
diff overlay, the terminal output and the debug console paint (Midnight: transparent over `Bg.Window`) — make it
opaque in a theme that wants a light code page; the syntax re-tint (`NeonHighlighting`) reads it to decide whether
an unknown xshd colour must be lifted (dark page) or sunk (light page). The context LEDs are `Status.*.Soft` /
`Progress.Context.Fill` at element opacity 0.15 (off) / 0.9 (on). Choices that differ from the table below (all
≤ Δ8 over the real background): `#3311132E` input boxes → `Surface.Input`; `#22000000` monogram chips →
`Surface.Sunken`; `#8C0A0C22` file-tree panel → `Sidebar.Background`, tab strip → `Bg.Panel`; `#7011152E`
terminal drawer / diff header → `Bg.Panel`; `#2628326E` result-row hover → `Surface.Raised`; `#20F38BA8` error
bar → `Status.Danger.Tint`; `#0A06071A` console surfaces → `Editor.Background`.

**Syntax colours by name.** `NeonHighlighting` picks a `Syntax.*` token from each xshd colour's NAME: comment →
`Comment`, string / char → `String`, number / digit → `Number`, preprocessor / regex / annotation → `Preprocessor`,
method / function → `Function`, class / type → `Type`, variable / property → `Variable`, control / goto → `Control`,
keyword / modifier / … → `Keyword`. AvalonEdit's built-in definitions keep their names; the bundled ones in
`Resources/Syntax/*.xshd` (GDScript, Godot shader, Godot resource files, JSONC, YAML, TOML, shell) use only names
that land in a token — `tests/syntaxtest` fails when one does not.

### Settings and the manager pages (team B)

Added for `SettingsView`, `FeaturesView`, `ModelManagerView`, `McpServersView`, `PermissionsView`, `PluginManagerView`,
`SkillsView`, `SubAgentsView`, `HookLibraryView`, `InstinctsView`, `SecurityShieldView` — every value there had no exact
token. Per rule 4, Classic.xaml and NeonCyber.xaml carry the **Midnight values** (marked "STILL MIDNIGHT'S VALUES") until
their theme team paints them; the last column names the general token each is closest to in meaning — a starting point.
(An alias entry — `<StaticResource x:Key="A" ResourceKey="B"/>` in the theme file — does NOT work: themetest T2 no longer
sees those keys in the loaded dictionary. Give every key a real value.)

| Token | Kind | Midnight value | Used for | Nearest general token |
|---|---|---|---|---|
| `Page.Header.Background` | gradient | linear `#7F141838` → `Transparent` (top → bottom) | header band of the manager pages | `Surface.Panel` |
| `Page.Footer.Background` | brush | `#88141838` | status strip at the bottom of a manager page | `Surface.Card.Flat` |
| `Page.Toolbar.Background` | brush | `#5511152E` | Security shield filter bar | `Surface.Sunken` |
| `Page.Background` | brush | `#181825` | a page that paints its own opaque background (MCP servers — its root used the app-level `MantleBrush`, which a view's own `Resources` cannot override on the root element) | `Bg.Panel` |
| `Page.Wash.Accent.2` · `.Accent.3` · `.Success` · `.Warning` | radial | radial (0.5,0 · 0.7 × 0.55) `#22A672FF` / `#22FF5EC4` / `#225CFFB0` / `#22FFD166` → `#06071A` | the coloured wash behind Sub-agents · Plugins + Security · Skills + Hooks · Instincts | `Backdrop.Nebula` |
| `Shadow.Panel.Soft` | effect | shadow `Black` blur 30 depth 0 opacity 0.5 | list / detail panels of the manager pages | `Shadow.Panel` |
| `Glow.Card.Hover` | effect | shadow `#4CDFFF` blur 14 depth 0 opacity 0.35 | a hovered list card | `Glow.Accent.1` |
| `IconTile.Accent.1-2` · `.Accent.2-1` · `.Accent.4-2` · `.Accent.4-3` · `.Accent.1-Success` · `.Success-Accent.1` · `.Warning-Accent.3` · `.Accent.3-Danger` | gradient | linear 0,0 → 1,1 between the two named accents (`#4CDFFF`→`#A672FF`, `#A672FF`→`#4CDFFF`, `#FF8AA6`→`#A672FF`, `#FF8AA6`→`#FF5EC4`, `#4CDFFF`→`#5CFFB0`, `#5CFFB0`→`#4CDFFF`, `#FFD166`→`#FF5EC4`, `#FF5EC4`→`#FF6E6E`) | the gradient icon squares (page headers, list cards, detail panes) | `Accent.Gradient` |
| `IconTile.Foreground` | brush | `White` | the glyph on an icon tile | `Text.OnAccent` |
| `Glow.IconTile.Accent.1` · `.Accent.2` · `.Accent.3` · `.Success` · `.Warning` | effect | shadow in that accent, blur 22 depth 0 opacity 0.55 | glow under a header / detail tile | `Glow.Accent.1/2/3`, `Glow.Success`, `Glow.Led.Warning` |
| `Glow.IconTile.Small.Accent.1` · `.Small.Accent.3` | effect | blur 16 opacity 0.4 (`#4CDFFF` / `#FF5EC4`) | Plugins list-card tiles | `Glow.Accent.1` / `.3` |
| `Glow.IconTile.Medium.Accent.1` | effect | `#4CDFFF` blur 18 opacity 0.5 | Skills list-card tile | `Glow.Accent.1` |
| `Glow.IconTile.Large.Success` | effect | `#5CFFB0` blur 26 opacity 0.6 | Hook library detail tile | `Glow.Success` |
| `Button.Action.Background` · `.Border` · `.Foreground` | brush | `#7028326E` · `#A0B4FF` @ 0.18 · `#E8ECFF` | the action buttons of the manager pages (`NeonAction` styles + inline header buttons) | `Button.Secondary.*` |
| `Button.Action.Hover.Background` · `.Hover.Border` | brush | `#B22E3870` · `#4CDFFF` | … hovered | `Button.Secondary.Hover.Background` · `Input.Focus.Border` |
| `Button.Action.Primary.Background` · `.Border` · `.Foreground` | gradient · brush · brush | linear 0,0→1,1 `#992C5A` → `#5A2A99` · `#FF5EC4` · `White` | Plugins primary button (Enable / Install); Security shield reuses Border + Foreground | `Button.Primary.*` |
| `Glow.Button.Action.Primary` | effect | `#FF5EC4` blur 18 opacity 0.45 | … its glow | `Shadow.Button` |
| `Button.Action.Accent.3.Background` · `Glow.Button.Action.Accent.3` | gradient · effect | linear 0,0→1,1 `#FF5EC4` → `#A672FF` · `#FF5EC4` blur 18 opacity 0.5 | Security shield primary button (Scan) | `Button.Primary.Background` · `Shadow.Button` |
| `Button.Action.Success.Background` · `.Border` · `.Foreground` · `Glow.Button.Action.Success` | gradient · brush · brush · effect | linear 0,0→1,1 `#5CFFB0` → `#4CDFFF` · `#5CFFB0` · `#06071A` · `#5CFFB0` blur 18 opacity 0.45 | Instincts primary button | `Button.Primary.*` · `Shadow.Button` |
| `SettingsNav.Foreground` · `.Hover.Background` · `.Selected.Background` · `.Selected.Foreground` · `.Indicator` | brush | `#C0C6EE` · `#1428326E` · `#2A4CDFFF` · `#E8ECFF` · `#4CDFFF` | the category list on the left of Settings | `Nav.Foreground` · `State.Hover` · `State.Selected` · `Nav.Selected.Foreground` · `Accent.1` |
| `Surface.Inset` | brush | `#22000000` | a recessed well: Hook library filter box, switch track | `Surface.Input` |
| `Accent.1.Tint.Faint` | brush | `#144CDFFF` | Settings › Model info card | `Accent.1.Tint` |
| `Status.Success.Tint.Faint` | brush | `#1A5CFFB0` | Settings › Provider "active provider" pill | `Status.Success.Tint` |
| `Status.Neutral.Tint` · `Status.Neutral.Border` | brush | `#1A8388BD` · `#558388BD` | Instincts brain chip while the brain is offline | `Surface.Chip` · `Border.Default` |
| `Status.Caution.Tint` | brush | `#44FAB387` | Features › activation "transfer to this PC" button | `Status.Warning.Tint` |
| `License.Tier.Background` · `License.Tier.Foreground` | gradient · brush | linear 0,0→1,0 `#A6E3A1` → `#94E2D5` · `#1E1E2E` | Features › activation tier badge | `License.Chip.Active.*` |
| `Badge.Quant.Background` · `Badge.Size.Background` · `Badge.Installed.Background` | brush | `#CBA6F7` / `#94E2D5` / `#A6E3A1` @ 0.15 | Model manager quantization / size / installed badges | `Accent.2.Tint` · `Accent.1.Tint` · `Status.Success.Tint` |
| `Fit.Excellent.Tint` · `Fit.Good.Tint` · `Fit.Partial.Tint` · `Fit.Poor.Tint` · `Fit.TooLarge.Tint` | brush | `#A6E3A1` / `#94E2D5` / `#F9E2AF` / `#FAB387` / `#F38BA8` @ 0.2 | Model manager hardware-fit pill (Excellent … Too large) | `Status.Success.Tint` · `Accent.1.Tint` · `Status.Warning.Tint` ×2 · `Status.Danger.Tint` |
| `Badge.Status.Foreground` · `Badge.Rule.Foreground` | brush | `#06071A` · `#0F1230` | dark text on a solid status pill: MCP server state, permission rule action (was the local `CrustBrush` / `BaseBrush` — a background colour used as text) | `Text.Inverse` |
| `Rating.Star` | brush | `Black` | Model manager star rating. It had no colour, so it showed the system black (dark on navy, hard to see); Midnight keeps that black. **Candidate fix** (needs a golden update): `Status.Warning` | `Status.Warning` |
| `Button.Plain.Background` · `.Border` · `.Foreground` | brush | `#DDDDDD` · `#707070` · `Black` | buttons that used to get the WINDOWS (Aero2) template and so ignored every theme: Settings › "Show" key / token toggles | `Button.Secondary.*` |
| `Button.Plain.Hover.Background` · `.Hover.Border` · `.Pressed.Background` · `.Pressed.Border` · `.Checked.Background` · `.Checked.Border` | brush | `#BEE6FD` · `#3C7FB1` · `#C4E5F6` · `#2C628B` · `#BCDDEE` · `#245A83` (the Aero2 values) | … their hover / pressed / checked states, also the Model manager list / grid switch and the Skills / Sub-agents Copy buttons (which are transparent until hovered) | `Button.Secondary.Hover.Background` · `Input.Focus.Border` · `State.Pressed` · `Surface.Raised` |
| `Input.Plain.Hover.Border` · `Input.Plain.Focus.Border` | brush | `#7EB4EA` · `#569DE5` (Aero2) | Hook library filter box border on hover / keyboard focus | `Input.Focus.Border` |
| `ListRow.Hover.Background` · `.Hover.Border` · `.Selected.Background` · `.Selected.Border` · `.Selected.Inactive.Background` · `.Selected.Inactive.Border` | brush | `#1F26A0DA` · `#A826A0DA` · `#3D26A0DA` · `#26A0DA` · `#3DDADADA` · `#DADADA` (Aero2) | Settings › Context & Memory list rows | `ListItem.Hover.Background` · `State.Selected` |

The `Button.Plain.*`, `Input.Plain.*` and `ListRow.*` tokens keep Midnight exactly as it rendered (Windows' own colours, now
behind tokens with the same template shape and trigger order as Aero2); Classic and Neon Cyber can finally restyle them.
They are **candidate Midnight fixes** too — e.g. `Button.Plain.*` → the `Button.Secondary.*` look — once the owner accepts
a new golden.

Team B mapped colours by meaning, not only by nearest value: `#3328326E` (snippet boxes) → `Surface.Raised.Strong` (Δ ≤ 1),
`#22FFFFFF` → `State.Hover.Strong` in a hover trigger and `Surface.Badge` on a badge, `#22000000` → `Surface.Inset` (not
`Bg.Overlay`), `#FF6B6B` / `#22FF6B6B` → `Status.Danger` / `.Tint` (Δ ≤ 3). The `HexToColor` bindings in Sub-agents,
Instincts and Security shield are data colours (agent colour, confidence, severity, category) that come from the models —
team C's part; the views keep binding them.

## Group C additions

Tokens added while converting the tool pages (Time Machine, Hex Editor, Debug Log, Task Manager), the License /
BrainX / DGX Spark dialogs, the preview pane and the C# colour code (converters, models, view models, markdown).
Midnight holds the exact colour each view hard-coded (pixel-identical). Classic and Neon Cyber LED hold values
designed from the approved mockups (Main.dc.html / License / BrainXInvite for Classic, NeonMain.dc.html for Neon);
the theme teams may retune them. A `<StaticResource x:Key="New" ResourceKey="Old"/>` alias does NOT work here:
the aliased keys are missing from a theme dictionary loaded by URI (themetest T2 caught it), so every theme
carries real values.

| Token | Kind | Midnight | Classic | Neon Cyber LED |
|---|---|---|---|---|
| **Dialogs (License, BrainX invite, DGX Spark, the code-built input dialog)** | | | | |
| `Dialog.Background` | gradient | linear `#FA171B42` → `#FA0A0C22` | linear `#FFFFFF` → `#ECECF0` → `#ECECF0` | linear `#F70E1228` → `#F7070812` |
| `Dialog.Background.Brainx` | gradient | linear `#FA1D1442` → `#FA0A0C22` | linear `#FFFFFF` → `#ECECF0` → `#ECECF0` | linear `#F7180E2C` → `#F7070812` |
| `Dialog.Background.Spark` | gradient | linear `#FA132A1E` → `#FA0A0C22` | linear `#F4FBEF` → `#ECECF0` → `#ECECF0` | linear `#F70C1A0A` → `#F7070812` |
| `Dialog.Border` | brush | `#55A672FF` | `#7E8199` | `#8000E5FF` |
| `Dialog.Border.Spark` | brush | `#5576D16B` | `#6E9A5A` | `#8CA6FF00` |
| `Shadow.Dialog` | effect | shadow `Black` blur 30 depth 0 opacity 0.65 | shadow `Black` blur 14 depth 3 opacity 0.35 | shadow `#00E5FF` blur 24 depth 0 opacity 0.35 |
| `Dialog.Card.Background` | brush | `#14FFFFFF` | `#FFFFFF` | `#0A00E5FF` |
| `Dialog.Card.Border` | brush | `#22FFFFFF` | `#A5A9BF` | `#4000E5FF` |
| `Dialog.Input.Background` | brush | `#33000000` | `#FFFFFF` | `#0A0E22` |
| `Dialog.Input.Border` | brush | `#44A672FF` | `#7F9DB9` | `#8C00E5FF` |
| `Dialog.Input.Border.Spark` | brush | `#5576D16B` | `#7F9DB9` | `#8CA6FF00` |
| `Dialog.Simple.Background` | brush | `#1E1E2E` | `#ECECF0` | `#0E1228` |
| `Dialog.Simple.Field` | brush | `#181825` | `#FFFFFF` | `#0A0E22` |
| `Dialog.Simple.Border` | brush | `#313244` | `#7F9DB9` | `#8000E5FF` |
| `Shadow.Dialog.Simple` | effect | shadow `Black` blur 24 depth 0 opacity 0.55 | shadow `Black` blur 14 depth 3 opacity 0.35 | shadow `#00E5FF` blur 20 depth 0 opacity 0.3 |
| `License.Avatar.Background` | gradient | linear `#FF5CA8` → `#A672FF` | linear `#F3D27A` → `#C08A2B` | linear `#FF2BD6` → `#8A2BE2` |
| `Glow.License.Avatar` | effect | shadow `#FF5CA8` blur 18 depth 0 opacity 0.6 | shadow `#8A5A10` blur 6 depth 1 opacity 0.35 | shadow `#FF2BD6` blur 18 depth 0 opacity 0.7 |
| `License.Plan.Best.Background` | brush | `#26A672FF` | `#FFFDF6` | `#1FFF2BD6` |
| `Button.Caution.Background` | brush | `#44FAB387` | linear `#FFFBEF` → `#FCE2A8` | `#33FFB000` |
| `Glow.Brainx.Avatar` | effect | shadow `#A672FF` blur 20 depth 0 opacity 0.7 | shadow `#1B3E86` blur 6 depth 1 opacity 0.35 | shadow `#FF2BD6` blur 20 depth 0 opacity 0.6 |
| `Spark.Avatar.Background` | gradient | linear `#9BE26B` → `#3FA34D` | linear `#8FD06B` → `#3E7B3E` | linear `#C8FF6B` → `#4F8A00` |
| `Spark.Avatar.Foreground` | brush | `#0B1A10` | `#FFFFFF` | `#071000` |
| `Glow.Spark.Avatar` | effect | shadow `#76D16B` blur 18 depth 0 opacity 0.6 | shadow `#24561A` blur 6 depth 1 opacity 0.35 | shadow `#A6FF00` blur 18 depth 0 opacity 0.7 |
| `Spark.Card.Active.Background` | brush | `#2276D16B` | `#F1F8EC` | `#1AA6FF00` |
| `Spark.Campaign.Background` | gradient | linear `#33FFD166` → `#1476D16B` | linear `#FFF6E2` → `#F1F8EC` | linear `#1FFFB000` → `#0FA6FF00` |
| `Spark.Campaign.Border` | brush | `#88FFD166` | `#E8A23A` | `#99FFB000` |
| **Tool pages (Debug Log, Hex Editor, Time Machine): backdrop, header strip, toolbars, the page icon tile** | | | | |
| `Page.Backdrop.DebugLog` | radial | radial `#22FF6E6E` → `#06071A` | radial `#FFFFFF` → `#ECECF0` | radial `#1AFF5C7A` → `#070812` |
| `Page.Backdrop.HexEditor` | radial | radial `#284CDFFF` → `#06071A` | radial `#FFFFFF` → `#ECECF0` | radial `#1400E5FF` → `#070812` |
| `Page.Backdrop.TimeMachine` | radial | radial `#33A672FF` → `#06071A` | radial `#FFFFFF` → `#ECECF0` | radial `#22FF2BD6` → `#070812` |
| `Page.Header.Background` | gradient | linear `#7F141838` → `Transparent` | linear `#FBFBFD` → `#F2F2F6` → `#E3E4EC` → `#B6B9CB` | linear `#B30E1228` → `Transparent` |
| `Page.Toolbar.Background` | brush | `#5511152E` | `#F2F2F5` | `#990B0F22` |
| `Page.Strip.Background` | brush | `#3311152E` | linear `#FFFFFF` → `#E5E6EE` | `#800B0F22` |
| `Page.StatusBar.Background` | brush | `#88141838` | `#ECECF0` | `#05060F` |
| `PageIcon.DebugLog.Background` | gradient | linear `#FF6E6E` → `#A672FF` | linear `#F4C2B4` → `#E07A5F` → `#D2563A` → `#8A3B2B` | linear `#FF5C7A` → `#FF2BD6` |
| `Glow.PageIcon.DebugLog` | effect | shadow `#FF6E6E` blur 22 depth 0 opacity 0.55 | shadow `#8A3B2B` blur 4 depth 1 opacity 0.35 | shadow `#FF5C7A` blur 20 depth 0 opacity 0.7 |
| `PageIcon.HexEditor.Background` | gradient | linear `#4CDFFF` → `#A672FF` | linear `#A9C4F0` → `#6C97E0` → `#316AC5` → `#1B3E86` | linear `#00E5FF` → `#0077FF` |
| `Glow.PageIcon.HexEditor` | effect | shadow `#4CDFFF` blur 18 depth 0 opacity 0.6 | shadow `#1B3E86` blur 4 depth 1 opacity 0.35 | shadow `#00E5FF` blur 18 depth 0 opacity 0.7 |
| `PageIcon.TimeMachine.Background` | gradient | linear `#FF5EC4` → `#A672FF` | linear `#C9BCF0` → `#8E7AD8` → `#5B45B0` → `#3A2A7A` | linear `#FF2BD6` → `#8A2BE2` |
| `PageIcon.TimeMachine.Halo` | radial | radial `#FF5EC4` → `#A672FF` → `Transparent` | radial `#408E7AD8` → `#205B45B0` → `Transparent` | radial `#FF2BD6` → `#8A2BE2` → `Transparent` |
| `Glow.PageIcon.TimeMachine` | effect | shadow `#FF5EC4` blur 20 depth 0 opacity 0.6 | shadow `#3A2A7A` blur 4 depth 1 opacity 0.35 | shadow `#FF2BD6` blur 20 depth 0 opacity 0.7 |
| **Buttons of the tool pages: glass (neutral), ghost (toolbar pill), accent (the magenta call to action)** | | | | |
| `Button.Glass.Background` | brush | `#7028326E` | linear `#FFFFFF` → `#F4F5F9` → `#DDE0EB` → `#C5C8D6` | `#1400E5FF` |
| `Button.Glass.Hover.Background` | brush | `#B22E3870` | linear `#FFFDF4` → `#FFF3D2` → `#FDD889` → `#E5A01A` | `#3300E5FF` |
| `Button.Glass.Border` | brush | `#A0B4FF` @ 0.18 | `#9A9DB4` | `#6600E5FF` |
| `Button.Glass.Hover.Border` | brush | `#4CDFFF` | `#E5A01A` | `#00E5FF` |
| `Button.Glass.Foreground` | brush | `#E8ECFF` | `#1C1E33` | `#E6F1FF` |
| `Button.Ghost.Background` | brush | `#2228326E` | linear `#FFFFFF` → `#E1E3EC` | `#0D00E5FF` |
| `Button.Ghost.Hover.Background` | brush | `#4428326E` | linear `#FFFDF4` → `#FDD889` | `#2600E5FF` |
| `Button.Accent.Background` | gradient | linear `#992C5A` → `#5A2A99` | linear `#A9C4F0` → `#6C97E0` → `#316AC5` → `#1B3E86` | `#2EFF2BD6` |
| `Button.Accent.Hover.Background` | gradient | linear `#B7367A` → `#7036C2` | linear `#C3D6F6` → `#8CB0EE` → `#3F7BD6` → `#1B3E86` | `#4DFF2BD6` |
| `Button.Accent.Border` | brush | `#FF5EC4` | `#1B3E86` | `#FF2BD6` |
| `Button.Accent.Foreground` | brush | `White` | `#FFFFFF` | `#FFFFFF` |
| `Glow.Button.Accent` | effect | shadow `#FF5EC4` blur 22 depth 0 opacity 0.45 | shadow `#1B3E86` blur 4 depth 1 opacity 0.3 | shadow `#FF2BD6` blur 18 depth 0 opacity 0.6 |
| **Lists, chips, scrims** | | | | |
| `List.Row.Background` | brush | `#0AFFFFFF` | `#F7F8FB` | `#0800E5FF` |
| `List.Row.Selected.Background` | brush | `#30A672FF` | `#C9D9F5` | `#3300E5FF` |
| `List.Header.Divider` | gradient | linear `#F2F2F2` → `#D5D5D5` | linear `#FFFFFF` → `#C5C8D6` → `#A0A3B8` | `#6600E5FF` |
| `Surface.Badge.Subtle` | brush | `#15FFFFFF` | `#F2F3F8` | `#1400E5FF` |
| `Text.OnStatus` | brush | `#06071A` | `#FFFFFF` | `#05060F` |
| `Accent.2.Border.Strong` | brush | `#66A672FF` | `#8A7CC0` | `#99FF2BD6` |
| `Shadow.Panel.Soft` | effect | shadow `Black` blur 30 depth 0 opacity 0.5 | shadow `Black` blur 10 depth 2 opacity 0.25 | shadow `#00E5FF` blur 20 depth 0 opacity 0.2 |
| `Bg.Scrim.Dialog` | brush | `#B2060718` | `#995C607E` | `#CC05060F` |
| `Bg.Scrim.Busy` | brush | `#D9060718` | `#CC5C607E` | `#E605060F` |
| **Diffs (Time Machine diff lines), git status (source control list), commit avatars** | | | | |
| `Diff.Added` | brush | `#5CFFB0` | `#24561A` | `#A6FF00` |
| `Diff.Deleted` | brush | `#FF6E6E` | `#A12B1F` | `#FF5C7A` |
| `Diff.Hunk` | brush | `#A672FF` | `#2D4FA8` | `#00E5FF` |
| `Diff.Context` | brush | `#C0C6EE` | `#4A4D68` | `#B9C4E8` |
| `Diff.Added.Background` | brush | `#245CFFB0` | `#E6F4DD` | `#1AA6FF00` |
| `Diff.Deleted.Background` | brush | `#24FF6E6E` | `#FBE3DF` | `#1AFF5C7A` |
| `Diff.Hunk.Background` | brush | `#20A672FF` | `#E9EEFA` | `#1400E5FF` |
| `Git.Added` | brush | `#7EE0A3` | `#24561A` | `#A6FF00` |
| `Git.Deleted` | brush | `#FF7A93` | `#A12B1F` | `#FF5C7A` |
| `Git.Renamed` | brush | `#8AB8FF` | `#2D4FA8` | `#00E5FF` |
| `Git.Modified` | brush | `#FFD37E` | `#9A6A00` | `#FFB000` |
| `Avatar.Ai` | gradient | linear `#A672FF` → `#4CDFFF` | linear `#6C97E0` → `#316AC5` | linear `#FF2BD6` → `#00E5FF` |
| `Avatar.Human` | gradient | linear `#4CDFFF` → `#5CFFB0` | linear `#8FD06B` → `#3E7B3E` | linear `#00E5FF` → `#A6FF00` |
| **Chat message roles (RoleToBrush / RoleToBackground converters)** | | | | |
| `Role.User` | brush | `#89B4FA` | `#1F3A93` | `#00E5FF` |
| `Role.Assistant` | brush | `#A6E3A1` | `#24561A` | `#A6FF00` |
| `Role.Exec` | brush | `#F9E2AF` | `#7A4A00` | `#FFB000` |
| `Role.Tool` | brush | `#94E2D5` | `#1B6F6A` | `#7DF4FF` |
| `Role.System` | brush | `#CBA6F7` | `#5B45B0` | `#FF8AE9` |
| `Role.Status` | brush | `#9C9CAC` | `#5A5D78` | `#7C88B8` |
| `Role.Default` | brush | `#CDD6F4` | `#1C1E33` | `#E6F1FF` |
| `Role.User.Background` | brush | `#2F3D5C` | `#E9EEFA` | `#1200E5FF` |
| `Role.Assistant.Background` | brush | `#313244` | `#FFFFFF` | `#B30A0E20` |
| `Role.Exec.Background` | brush | `#3A3530` | `#FFF6E2` | `#14FFB000` |
| `Role.Tool.Background` | brush | `#2D3A3E` | `#EEF6F5` | `#0F7DF4FF` |
| `Role.Status.Background` | brush | `#1E1E2E` | `#F2F3F8` | `#80070812` |
| **Chat markdown (Helpers/MarkdownHelper): text, headings, quotes, tables, code blocks and their syntax colours** | | | | |
| `Markdown.Text` | brush | `#CDD6F4` | `#1C1E33` | `#D5DEFA` |
| `Markdown.Muted` | brush | `#6C7086` | `#5A5D78` | `#7C88B8` |
| `Markdown.Heading` | brush | `#CBA6F7` | `#2D4FA8` | `#FF8AE9` |
| `Markdown.Heading.Minor` | brush | `#89B4FA` | `#316AC5` | `#7DF4FF` |
| `Markdown.Emphasis` | brush | `#F5E0DC` | `#4A4D68` | `#E6F1FF` |
| `Markdown.Link` | brush | `#89B4FA` | `#1F3A93` | `#00E5FF` |
| `Markdown.Rule` | brush | `#45475A` | `#C5C8D6` | `#4000E5FF` |
| `Markdown.Quote.Background` | brush | `#18CBA6F7` | `#F2F3F8` | `#B30A0E20` |
| `Markdown.Quote.Border` | brush | `#CBA6F7` | `#316AC5` | `#8000E5FF` |
| `Markdown.Code` | brush | `#94E2D5` | `#1B6F6A` | `#7DF4FF` |
| `Markdown.Code.Background` | brush | `#313244` | `#F2F3F8` | `#0B0F22` |
| `Markdown.Border` | brush | `#313244` | `#C5C8D6` | `#4000E5FF` |
| `Markdown.CodeBlock.Background` | brush | `#11111B` | `#FFFFFF` | `#05060F` |
| `Markdown.Diff.Added` | brush | `#A6E3A1` | `#24561A` | `#A6FF00` |
| `Markdown.Diff.Added.Background` | brush | `#30A6E3A1` | `#E6F4DD` | `#1AA6FF00` |
| `Markdown.Diff.Deleted` | brush | `#F38BA8` | `#A12B1F` | `#FF5C7A` |
| `Markdown.Diff.Deleted.Background` | brush | `#30F38BA8` | `#FBE3DF` | `#1AFF5C7A` |
| `Markdown.Diff.Hunk` | brush | `#89B4FA` | `#2D4FA8` | `#00E5FF` |
| `Markdown.Syntax.Text` | brush | `#CDD6F4` | `#1C1E33` | `#D5DEFA` |
| `Markdown.Syntax.Comment` | brush | `#6C7086` | `#3E7B3E` | `#5E6A94` |
| `Markdown.Syntax.String` | brush | `#A6E3A1` | `#A12B1F` | `#A6FF00` |
| `Markdown.Syntax.Number` | brush | `#FAB387` | `#7A4A00` | `#FFB000` |
| `Markdown.Syntax.Keyword` | brush | `#CBA6F7` | `#1F3A93` | `#FF2BD6` |
| `Markdown.Syntax.Type` | brush | `#F9E2AF` | `#1B6F6A` | `#00E5FF` |
| **Time Machine: commit spheres, the timeline spine, the rewind beam and overlay** | | | | |
| `TimeMachine.Halo.Ai` | radial | radial `#A672FF` → `Transparent` | radial `#806C97E0` → `Transparent` | radial `#FF2BD6` → `Transparent` |
| `TimeMachine.Halo.Human` | radial | radial `#4CDFFF` → `Transparent` | radial `#808FD06B` → `Transparent` | radial `#00E5FF` → `Transparent` |
| `TimeMachine.Sphere.Ai` | radial | radial `#FFFFFF` → `#A672FF` → `#4F2A8C` | radial `#FFFFFF` → `#6C97E0` → `#1B3E86` | radial `#FFFFFF` → `#FF2BD6` → `#6A0F5A` |
| `TimeMachine.Sphere.Ai.Stroke` | brush | `#7E4DCE` | `#1B3E86` | `#FF2BD6` |
| `TimeMachine.Sphere.Human` | radial | radial `#FFFFFF` → `#4CDFFF` → `#1D6E8C` | radial `#FFFFFF` → `#8FD06B` → `#24561A` | radial `#FFFFFF` → `#00E5FF` → `#005F6E` |
| `TimeMachine.Sphere.Human.Stroke` | brush | `#286E94` | `#24561A` | `#00E5FF` |
| `TimeMachine.Highlight` | brush | `#80FFFFFF` | `#CCFFFFFF` | `#80FFFFFF` |
| `Glow.TimeMachine.Head` | effect | shadow `#5CFFB0` blur 14 depth 0 opacity 0.8 | shadow `#3FAE3F` blur 8 depth 0 opacity 0.6 | shadow `#A6FF00` blur 14 depth 0 opacity 0.9 |
| `TimeMachine.Spine` | gradient | linear `#4CDFFF` → `#A672FF` → `#FF5EC4` → `#5CFFB0` | linear `#6C97E0` → `#316AC5` → `#5B45B0` → `#3FAE3F` | linear `#A6FF00` → `#00E5FF` → `#FF2BD6` |
| `TimeMachine.Beam` | gradient | linear `Transparent` → `#FF5EC4` → `#4CDFFF` → `#FF5EC4` → `Transparent` | linear `Transparent` → `#316AC5` → `#6C97E0` → `#316AC5` → `Transparent` | linear `Transparent` → `#FF2BD6` → `#00E5FF` → `#FF2BD6` → `Transparent` |
| `Glow.TimeMachine.Beam` | effect | shadow `#FF5EC4` blur 40 depth 0 opacity 0.9 | shadow `#316AC5` blur 20 depth 0 opacity 0.6 | shadow `#FF2BD6` blur 40 depth 0 opacity 0.9 |
| `TimeMachine.CornerGlow` | radial | radial `#3CFF5EC4` → `Transparent` | radial `#26316AC5` → `Transparent` | radial `#3CFF2BD6` → `Transparent` |
| `Glow.TimeMachine.Avatar` | effect | shadow `#A672FF` blur 22 depth 0 opacity 0.5 | shadow `#1B3E86` blur 6 depth 1 opacity 0.35 | shadow `#FF2BD6` blur 22 depth 0 opacity 0.6 |
| `Glow.TimeMachine.Rewind` | effect | shadow `#FF5EC4` blur 22 depth 0 opacity 0.5 | shadow `#1B3E86` blur 6 depth 1 opacity 0.35 | shadow `#FF2BD6` blur 22 depth 0 opacity 0.6 |
| `Glow.Accent.3.Small` | effect | shadow `#FF5EC4` blur 10 depth 0 opacity 0.8 | shadow `#316AC5` blur 4 depth 0 opacity 0.4 | shadow `#FF2BD6` blur 10 depth 0 opacity 0.9 |
| `TimeMachine.Rewind.Orb` | radial | radial `#FF5EC4` → `#A672FF` | radial `#A9C4F0` → `#316AC5` | radial `#FF2BD6` → `#8A2BE2` |
| `Glow.TimeMachine.Rewind.Orb` | effect | shadow `#FF5EC4` blur 44 depth 0 opacity 0.9 | shadow `#316AC5` blur 24 depth 0 opacity 0.6 | shadow `#FF2BD6` blur 44 depth 0 opacity 0.9 |
| `TimeMachine.Rewind.Title` | gradient | linear `#FF5EC4` → `#4CDFFF` | linear `#FFFFFF` → `#DCE6FA` | linear `#FF2BD6` → `#00E5FF` |
| **Preview pane (built-in browser): toolbar, origin badges, the web-page canvas** | | | | |
| `Preview.Toolbar.Background` | brush | `#8C0A0C22` | linear `#FBFBFD` → `#F2F2F6` → `#E3E4EC` → `#B6B9CB` | `#F20B0F22` |
| `Preview.Badge.Local.Background` | brush | `#332ECC71` | `#D5ECC8` | `#33A6FF00` |
| `Preview.Badge.External.Background` | brush | `#55FF8A3D` | `#FCE2A8` | `#40FFB000` |
| `Preview.Driving.Background` | brush | `#33FFB020` | `#FCE2A8` | `#33FFB000` |
| `Preview.Page.Button.Background` | brush | `#3328326E` | linear `#FFFFFF` → `#E2E3EA` | `#2600E5FF` |
| `Preview.Page.Button.Hover.Background` | brush | `#5528326E` | linear `#FFFDF4` → `#FDD889` | `#4000E5FF` |
| `Preview.Page.Background` | brush | `White` | `White` | `White` |
| `Preview.Page.Foreground` | brush | `#30344A` | `#30344A` | `#30344A` |
| `Preview.Page.Muted` | brush | `#60657A` | `#60657A` | `#60657A` |
| `Preview.Page.Icon` | brush | `#9098B0` | `#9098B0` | `#9098B0` |

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
| `Views/CodeEditorView.xaml.cs` caret / selection | `#4CDFFF` / `#59A672FF` | done (A) — `Editor.Caret` (re-applied on ThemeChanged) / `Editor.Selection` (resource reference) |
| `Views/Editor/NeonHighlighting.cs` | violet / cyan / mint / `#FFC66D` / magenta / `#7E8AA8` | done (A) — `Syntax.*`, re-tinted from the xshd originals when the palette changes |
| `Views/Editor/MinimapControl.cs` | bar, comment, viewport, viewport pen, hover, caret, change | done (A) — `Editor.Minimap.*`, read on every paint |
| `Views/Editor/BreakpointMargin.cs` | breakpoint, halo, execution, gutter | done (A) — `Editor.Breakpoint` / `.Breakpoint.Halo` / `.ExecutionLine` (`.Fill`, `.Border`) / `.Gutter`, read on every paint |
| `Views/Editor/AgentGlowRenderer.cs` | `#A672FF` | done (A) — `Editor.AgentGlow`, read on every draw |
| `Views/Editor/MatchHighlight.cs` | `#66FFC54C` / `#FFF4D6` | done (A) — resource references on the Run |
| `ViewModels/MainViewModel.cs` `GpuTempBrush` | Catppuccin red / peach / yellow / green | `Status.Danger.Soft` / `Status.Caution.Soft` / `Status.Warning.Soft` / `Status.Success.Soft` |
| `ViewModels/MainViewModel.cs` + `CommandPalette.cs` `KindColor` strings | `#4CDFFF` `#A672FF` `#76D16B` … | `Accent.1`, `Accent.2`, `Brand.Spark` … (hand the bound brush, not a hex string) |
| `Converters/Converters.cs` (62) | diff kinds, AI/user avatars, file status, context-usage ramp | `Status.*`, `Accent.*`, `Chart.Series.*` — add `Diff.*` tokens for the diff colours |
| `Models/*` (LogEntry levels, SecurityRule severities, SubAgent colours, Instinct, Buddy rarity, CodeEditorModels git badges) | literal hex | `Status.*` / `Accent.*`; add `Severity.*` / `Git.*` tokens if the meaning needs its own colour |
| `Services/Helpers/InputDialog.cs`, `Views/Editor/ChoiceDialog.cs` (done (A): SetResourceReference) | `TryFindResource(legacy key)` | the same call with the token name |
| `MainWindow.xaml.cs` DEV version chip | `#FF6E6E` | done — `SetResourceReference(…, "Status.Danger")` |

## Theme: Classic (Windows XP Silver)

`Resources/Themes/Classic.xaml` — design `Main` / `Settings` / `License` / `BrainXInvite` `.dc.html`. Every sales-video
recording uses Classic, so a converted view must look intentional here, not only in Midnight.

| Role | Classic | Role | Classic |
|---|---|---|---|
| window face `Bg.Window` | `#ECECF0` | panel / elevated | `#F4F4F8` / `#FCFCFE` |
| text primary / secondary / muted / faint | `#1C1E33` / `#4A4D68` / `#63667F` / `#767990` | link | `#1F3A93` |
| Luna blue `Accent.1`, `Focus.Ring` | `#316AC5` | accents 2 / 3 / 4 | `#5B4AA8` / `#C2600E` / `#C24E6E` |
| borders subtle / default / strong | `#C5C8D6` / `#A5A9BF` / `#7E8199` | edit field border `Input.Border` | `#7F9DB9` |
| success / warning / danger / info | `#2B7F2B` / `#A86A0C` / `#C42B1C` / `#2F63B8` | tooltip, update banner | `#FFFFE1` |
| fonts `Font.Ui*` / `Font.Mono*` | Tahoma, Leelawadee UI / Consolas, Lucida Console, Tahoma | editor | white page, VS-2005 syntax colours |

Metal follows the owner's xp.css recipe: multi-stop gradients (bright band at 8 %, fold at 50 % on gels, a dark line at
the bottom), amber `#FDD889 → #E5A01A` for hover. Metal is used only on short chrome; tall panes get near-flat gradients.
The title bar and status bar are square because they touch the window edge.

**Where Classic means something different from Midnight (check when converting a view)**
* Light theme: lighter = higher. `Text.Inverse` is **white** (text on a strong fill), and `Status.*.Soft` are mid-dark
  tones: they work as text on silver and as a fill under white `Text.Inverse`.
* `State.*` are Luna-blue washes, and `Surface.Badge` / `State.Fill.Faint` are faint *dark* washes, because a white
  overlay disappears on silver. `State.Selected` is solid `#E1E9F7`.
* Glows: XP has none. Most `Glow.*` have Opacity 0, `Glow.Accent.*` are faint halos, and `Glow.StatusBar.Chip` is
  the amber hover.
* **The ≈ rows of the hard-coded table were matched by pixels on Midnight's navy, not by role.** In Classic, a faint tint
  mapped to `Text.Inverse` (`#1AF38BA8`, `#1AFFD166`, `#FA132A1E`) paints white. One mapped to `Border.Strong`
  (`#30F9E2AF`, `#3CFF5EC4`) paints an opaque `#7E8199`. The same goes for `Bg.Overlay`, `Bg.Panel.Solid`,
  `Border.Subtle`, `State.Hover`, `Editor.*`. Pick the token whose role matches (usually a `*.Tint`), or add one.
* Tahoma is about 5 % wider than Segoe UI, so check tight, fixed-width text.

**Control styles.** `Theme.xaml` is merged *after* the theme slot, and themetest T2 rejects a string key that Midnight
lacks. So Classic.xaml adds only **implicit (type-keyed) styles**, for controls that `Theme.xaml` does not style
implicitly: Button, ToggleButton, TextBox, ScrollBar, ListBoxItem, TabControl / TabItem, RadioButton and Hyperlink.

* A control without a Style gets the XP look automatically, and the system look in Midnight, as before. Local values
  such as `Background="Transparent"` and `BorderThickness="0"` are respected, so icon buttons stay flat.
* Keyed styles (PrimaryButton, SecondaryButton, NavButton, ActivityBarButton, the caption buttons, Card) and Theme.xaml's
  implicit ComboBox / CheckBox / Slider / ToolTip are restyled through tokens only. Their corner radii and shapes stay
  as Theme.xaml draws them.
* The caption-button hover tokens are VisualBrushes that draw a real XP caption button. The rest state cannot be drawn
  that way, because the template paints a literal Transparent.
* Changing a keyed template per theme would need a token hook in Theme.xaml, which is a foundation decision.

**Legacy palette — deliberately left at Midnight's colours** (only `NeonUiFont`, `NeonMonoFont`, `MainFont` and `MonoFont`
are Classic's).

* The 7 local-override views read Catppuccin keys locally (light text) and `Metal*` at app level. Classic-light `Metal*`
  values would put that light text on light fills.
* The other views pair `Neon*` keys with about 400 hard-coded dark colours.

So an unconverted view stays a readable Midnight island inside the Classic shell until its team converts it. After the
A/B/C merge, grep for any remaining legacy `StaticResource` key: in Classic it still shows a Midnight colour.
`ThemeInfo.IsReady` is `true` for all three themes since 2026-10-11 (every page rendered with `tests/themetest --all-pages`).

## Theme: Neon Cyber LED (`NeonCyber.xaml`)

Design: `NeonMain.dc.html` (+ the Neon card of `ThemePicker.dc.html`). Near-black HUD, a faint 32 px cyan grid,
cyan LED strips on every edge, magenta for whatever is active, and glows instead of drop shadows.

**Colour roles** — keep them when you add a token or convert a view, so a converted page looks intentional here too:

| Role | Colour | Tokens |
|---|---|---|
| Structure, focus, links, borders, the grid | cyan `#00E5FF` | `Accent.1`, `Focus.Ring`, `Text.Link`, `Border.*` (cyan at 16 / 32 / 50 %), `Input.*`, `Card.Border` |
| Action + "you are here" | magenta `#FF2BD6` | `Accent.2`, `State.Selected*`, `Button.Primary.*`, `Button.Secondary.Border`, `ActivityBar.Checked.*`, `Nav.Selected.*` |
| Success, lit LEDs, checkboxes | lime `#A6FF00` (text `#C8FF6B`) | `Status.Success*`, `CheckBox.Checked.*`, `StatusBar.Brain.*`, buddy chip |
| Warning, cost, execution line | amber `#FFB000` | `Status.Warning*`, `Editor.ExecutionLine*`, `Syntax.Number` |
| Danger · info · third accent | `#FF5C7A` · `#3DA5FF` · violet `#B45CFF` | `Status.Danger*` · `Status.Info*` · `Accent.3` |
| Brands | unchanged from Midnight | `Brand.Pro`, `Brand.Spark`, `Spark.Chip.*` |

**Text ladder** on `#070812`: `Text.Primary #E6F1FF` › `Body #D5DEFA` › `Secondary #B9C4E8` › `Tertiary #9AA6D0` ›
`Muted #8692C2` › `Faint #7682B2`. Every step stays ≥ 4.5:1 on the window, sidebar, card, input, title-bar and
selected surfaces. The weakest pair is `Faint` on a selected row, at 4.81:1. `Syntax.Comment` = `Faint`. Only `Editor.LineNumber #63709C` (3.7–4.1:1) is
below — line numbers are not body text. White on `Banner.Button.Background` is ≥ 5.2:1 (bright `#FF2BD6` under
white would be 3.2:1, so the button uses deep magenta → violet).

**Fonts**: `Chakra Petch, Bahnschrift, Leelawadee UI, Segoe UI` for UI, `JetBrains Mono, Cascadia Mono/Code` for
mono. Chakra Petch (the mock's face) is used where installed; Bahnschrift (DIN-style, in every Windows 10+) is
what renders otherwise. Leelawadee UI is named on purpose: Bahnschrift has no Thai. *TH Chakra Petch* (the Thai
original) is NOT listed: its Latin cap height is 0.475 em vs ~0.70 for UI faces, so every label would shrink by a third.

**Kinds that differ from Midnight** (all allowed — T2 accepts any `Brush` for a brush token):
`Backdrop.Nebula` is a tiled `DrawingBrush` (the grid), `TitleBar.Border` / `StatusBar.Border` are horizontal
`LinearGradientBrush`es (cyan → magenta, lime → cyan LED strips). `ThemeService.ColorOf` on them returns the
first stop / the fallback — no C# reads them. **`Bg.*` stay solid on purpose** so converters can `ColorOf` them.

**Shadows are glows**: `Shadow.Card` (cyan 22 %), `Shadow.Button` (magenta 40 %), `Shadow.Popup` / `Shadow.Palette`
(cyan) all have `ShadowDepth 0`. `Glow.StatusBar.Pip` is white because the pip it sits under is red, amber or lime.

**Legacy palette**: every legacy key holds the Neon Cyber value of the token it maps to (tables above), so an
unconverted view already wears this theme when it is built and converting it changes nothing on screen. Mind the
names: `NeonViolet` = `Accent.2` = magenta here, `NeonMagenta` = `Accent.3` = violet, `NeonMint` = lime. The
Catppuccin keys follow their app-level role (`BaseBrush` = dialog background, `TextBrush` = body text, …).

**Not expressible as tokens** (needs a style change in `Theme.xaml` or a per-theme style dictionary — a style keyed
in the theme slot is shadowed because `Theme.xaml` merges after it): the mock's square corners (the shared styles
hard-code `CornerRadius` 6-12), the 1 px cyan outline on idle dock icons, corner brackets on cards, and letter-spaced
uppercase labels.

## Fx — the playful extras (every theme)

| Key | Type | What |
|---|---|---|
| `Fx.Flourish` | `Visibility` | `Collapsed` hides the extras: the girl beside an agent's question, the light sweeping a live status line. Classic = `Collapsed`. |
| `Fx.Sweep.Edge` / `Fx.Sweep.Mid` | `Color` | the sweeping band over a live status line (transparent edge → tinted middle). |
| `Fx.Card.Glow` | `DropShadowEffect` | the halo around the question card (Opacity 0 = none, Classic). |
