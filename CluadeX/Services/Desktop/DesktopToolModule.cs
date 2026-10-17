using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using CluadeX.Models;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;

namespace CluadeX.Services.Desktop;

/// <summary>
/// The desktop_* tools: see and drive other programs on this PC the way a tester does — list windows, screenshot
/// one, read its UI Automation tree, find elements, act on them by ref (Invoke / Select / Toggle / Value / Scroll,
/// a real mouse click when no pattern fits), type, press keys, launch a program, wait for something to appear.
/// Built for testing a WPF / WinForms app, a game window or an installer.
///
/// Safety (see <see cref="DesktopSafety"/>): permission scope "desktop" with the app (process name) as resource —
/// the first call on an app in a run asks, "Allow in this chat" covers the chat, rules like "desktop(Godot*)"
/// decide per app, and the permission switch does not lift it. Password fields, credential / UAC / Windows Security
/// windows, the secure desktop and CluadeX's own approval UI are refused whatever the rules say. Every action is
/// logged with app, action and element name (never the typed text). The run's Stop cancels waits and launches.
/// </summary>
public sealed class DesktopToolModule : IAgentToolModule
{
    private readonly SettingsService? _settings;
    private readonly ToolImageStore? _images;
    private readonly DebugLogService? _log;
    private readonly Func<ToolType, string?>? _gateOverride;
    private readonly DesktopAutomation _uia = new();

    public DesktopToolModule(SettingsService settings, ToolImageStore? images = null, DebugLogService? log = null)
        : this(settings, images, log, null) { }

    /// <summary>Harness constructor: <paramref name="gateOverride"/> replaces the Settings switch.</summary>
    internal DesktopToolModule(SettingsService? settings, ToolImageStore? images, DebugLogService? log, Func<ToolType, string?>? gateOverride)
    {
        _settings = settings;
        _images = images;
        _log = log;
        _gateOverride = gateOverride;
    }

    public string Id => "desktop";

    private static readonly Dictionary<ToolType, string> Names = new()
    {
        [ToolType.DesktopWindows] = "desktop_windows",
        [ToolType.DesktopScreenshot] = "desktop_screenshot",
        [ToolType.DesktopInspect] = "desktop_inspect",
        [ToolType.DesktopFind] = "desktop_find",
        [ToolType.DesktopAct] = "desktop_act",
        [ToolType.DesktopType] = "desktop_type",
        [ToolType.DesktopKeys] = "desktop_keys",
        [ToolType.DesktopLaunch] = "desktop_launch",
        [ToolType.DesktopWait] = "desktop_wait",
    };

    private static readonly Dictionary<string, ToolType> ByName = BuildByName();

    private static Dictionary<string, ToolType> BuildByName()
    {
        var d = new Dictionary<string, ToolType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, n) in Names) d[n] = t;
        d["desktop_list_windows"] = ToolType.DesktopWindows;
        d["desktop_capture"] = ToolType.DesktopScreenshot;
        d["desktop_tree"] = ToolType.DesktopInspect;
        d["desktop_snapshot"] = ToolType.DesktopInspect;
        d["desktop_click"] = ToolType.DesktopAct;
        d["desktop_key"] = ToolType.DesktopKeys;
        d["desktop_press"] = ToolType.DesktopKeys;
        d["desktop_start"] = ToolType.DesktopLaunch;
        d["desktop_wait_for"] = ToolType.DesktopWait;
        return d;
    }

    public IReadOnlyCollection<ToolType> ToolTypes => Names.Keys;

    /// <summary>Testing a desktop app is project work; a plain chat does not pay for nine schemas.</summary>
    public bool WorksWithoutProject => false;

    public ToolType? ResolveToolType(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && ByName.TryGetValue(toolName.Trim(), out var t) ? t : null;

    public static string NameOf(ToolType t) => Names.TryGetValue(t, out var n) ? n : t.ToString();

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    /// <summary>The prompt section is <see cref="DesktopPromptSection"/> (an IPromptSectionProvider).</summary>
    public string? GetPromptSection(PromptSectionContext context) => null;

    // ─────────────────────────── gate ───────────────────────────

    public string? GetRefusal(ToolType type)
    {
        if (!OperatingSystem.IsWindows()) return L("desktop.windowsOnly");
        if (_gateOverride != null) return _gateOverride(type);
        return _settings?.Settings.DesktopControlEnabled == false ? L("desktop.off") : null;
    }

    // ─────────────────────────── schemas ───────────────────────────

    private static readonly (string, string, string, bool)[] Target =
    {
        ("title", "string", "Window title: a regex or part of it (front-most match)", false),
        ("pid", "integer", "Process id instead of title", false),
        ("handle", "string", "Window handle from desktop_windows (0x…)", false),
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        yield return Schema("desktop_windows",
            "List the top-level windows on this PC (front first): handle, title, process, pid, bounds in screen pixels, which is in front.",
            P("filter", "string", "Only windows whose title or process contains this (or /regex/)"));
        yield return Schema("desktop_screenshot",
            "Capture one window (even if covered) or a screen region as PNG; saved to disk and attached as an image. Physical pixels, all monitors (coordinates may be negative).",
            Target.Concat(new[]
            {
                P("region", "string", "'x,y,width,height' in screen pixels instead of a window"),
                P("max_edge", "integer", "Longest edge of the image sent to you (default 1568; the PNG on disk is full size)"),
            }).ToArray());
        yield return Schema("desktop_inspect",
            "A window's UI Automation tree (or under a ref): one line per element with its ref (e12), type, name, automation id, bounds, state and patterns. Act on refs with desktop_act.",
            Target.Concat(new[]
            {
                P("ref", "string", "Inspect under this element instead of the whole window"),
                P("depth", "integer", "Levels below the root (default 6, max 30)"),
                P("limit", "integer", "Most elements (default 150, max 1000)"),
            }).ToArray());
        yield return Schema("desktop_find",
            "Find elements in a window by name, automation id, control type or text (substring, or /regex/). Returns refs for desktop_act.",
            Target.Concat(new[]
            {
                P("name", "string", "Element name contains"), P("automation_id", "string", "AutomationId contains"),
                P("control_type", "string", "Button, Edit, CheckBox, ListItem, TabItem, MenuItem, DataItem…"),
                P("text", "string", "Name, help text or value contains"), P("ref", "string", "Search under this element"),
                P("limit", "integer", "Most matches (default 20)"),
            }).ToArray());
        yield return Schema("desktop_act",
            "Act on an element by ref: invoke (buttons, links, rows — falls back to a child button, Select, then a real click), select (list/tab items), toggle (check boxes), expand / collapse, set_value, scroll, focus, click / double_click / right_click (real mouse at its centre).",
            P("ref", "string", "Element ref from desktop_inspect / desktop_find", true),
            P("action", "string", "invoke | select | toggle | expand | collapse | set_value | scroll | focus | click | double_click | right_click", true),
            P("value", "string", "set_value: the new value"),
            P("direction", "string", "scroll: down | up | left | right (none = scroll it into view)"),
            P("amount", "integer", "scroll: steps (default 3)"));
        yield return Schema("desktop_type",
            "Type text into the focused element, or into ref (focused first): sets the Value directly when the control allows it, else real Unicode key input. Never into password fields.",
            P("text", "string", "Text to type", true), P("ref", "string", "Element to type into"),
            P("clear", "boolean", "Replace the current text instead of appending"), P("submit", "boolean", "Press Enter afterwards"));
        yield return Schema("desktop_keys",
            "Press keys in a window with real keyboard input, in order: 'ctrl+s', 'Tab Tab Enter', 'alt+F4', 'F5'. The window is brought to the front first. Windows-key chords are refused.",
            Target.Concat(new[]
            {
                P("keys", "string", "Space-separated chords", true), P("ref", "string", "Focus this element first"),
                P("repeat", "integer", "Repeat the sequence (1-50)"), P("hold_ms", "integer", "Hold each chord this long"),
            }).ToArray());
        yield return Schema("desktop_launch",
            "Start a program (exe path, or a name on PATH) and wait until its main window shows; returns pid and window.",
            P("path", "string", "Program to start", true), P("args", "string", "Command-line arguments"),
            P("cwd", "string", "Working directory (default: the project)"),
            P("timeout_ms", "integer", "How long to wait for a window (default 20000, max 120000)"));
        yield return Schema("desktop_wait",
            "Wait until a window (title) or an element in a window (name / automation_id / control_type / text) appears — or disappears. Polls; the run's Stop ends it.",
            Target.Concat(new[]
            {
                P("name", "string", "Element name contains"), P("automation_id", "string", "AutomationId contains"),
                P("control_type", "string", "Element control type"), P("text", "string", "Element name / value contains"),
                P("state", "string", "appear (default) | disappear"),
                P("timeout_ms", "integer", "Default 10000, max 120000"),
            }).ToArray());
    }

    private static (string, string, string, bool) P(string name, string type, string desc, bool required = false) => (name, type, desc, required);

    private static ToolSchema Schema(string name, string description, params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in props)
        {
            properties[p.Name] = new Dictionary<string, string> { ["type"] = p.Type, ["description"] = p.Desc };
            if (p.Required) required.Add(p.Name);
        }
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
        // Deferred: a model with tool search loads them when a task needs the desktop.
        return new ToolSchema { Name = name, Description = description, InputSchema = doc.RootElement.Clone(), DeferLoading = true };
    }

    // ─────────────────────────── permission ───────────────────────────

    /// <summary>The app the gate judged for a call: the target must still be that app when the call runs (a
    /// window can close and another take its title while the user answers the prompt).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolCall, string> _judged = new();

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        if (call.Type == ToolType.DesktopWindows) return ToolPermission.Free("desktop", "(window list)");
        string? app;
        string? detail = null;
        if (call.Type == ToolType.DesktopLaunch)
        {
            string path = call.GetArg("path").Trim().Trim('"');
            app = Path.GetFileNameWithoutExtension(path);
            detail = L("desktop.perm.launch") + " " + (path + " " + call.GetArg("args")).Trim();
        }
        else
        {
            app = AppOf(call, context);
            if (app == null) return ToolPermission.Free("desktop", "(no target)");   // the call fails on its own
            detail = string.Format(L("desktop.perm.act"), app, NameOf(call.Type));
        }
        if (string.IsNullOrWhiteSpace(app)) app = "unknown";
        _judged.AddOrUpdate(call, app);
        return new ToolPermission
        {
            Scope = "desktop",
            Resource = app,
            Detail = detail,
            OnceKey = $"desktop:{app}@{RunKey()}",
            ChatScopeKey = "desktop-chat:" + app,
            AskEvenWhenPermissionsOff = true,
        };
    }

    /// <summary>The run a call belongs to (the top-level run, so a sub-agent shares its parent's approval).</summary>
    private static string RunKey()
    {
        var s = AgentRunScope.Current;
        while (s?.Parent != null) s = s.Parent;
        return s?.TurnId ?? "no-run";
    }

    /// <summary>The process name of the app a call acts on, or null when the target cannot be resolved.</summary>
    private string? AppOf(ToolCall call, ToolModuleContext context)
    {
        try
        {
            using var dpi = DesktopNative.PerMonitorDpi();
            if (_uia.Lookup(context.SessionKey, call.GetArg("ref")) is { } r) return r.ProcessName;
            if (call.Type == ToolType.DesktopScreenshot && !string.IsNullOrWhiteSpace(call.GetArg("region")) && !HasTarget(call)) return "screen";
            bool fgDefault = call.Type is ToolType.DesktopKeys or ToolType.DesktopType;
            if (call.Type == ToolType.DesktopWait && !HasTarget(call)) return null;
            return DesktopWindows.Resolve(call.GetArg("handle"), call.GetArg("pid"), call.GetArg("title"), fgDefault, out _)?.ProcessName;
        }
        catch { return null; }
    }

    private static bool HasTarget(ToolCall call) =>
        !string.IsNullOrWhiteSpace(call.GetArg("handle")) || !string.IsNullOrWhiteSpace(call.GetArg("pid")) || !string.IsNullOrWhiteSpace(call.GetArg("title"));

    // ─────────────────────────── execute ───────────────────────────

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (call.Type != ToolType.DesktopWindows && DesktopSafety.SecureDesktopRefusal() is { } sec) return Fail(call, sec);
            if (_judged.TryGetValue(call, out var judged) && call.Type != ToolType.DesktopLaunch)
            {
                string? now = AppOf(call, context);
                if (now != null && !string.Equals(now, judged, StringComparison.OrdinalIgnoreCase))
                    return Fail(call, $"The target changed while permission was asked: it was {judged}, now it is {now}. Call again to act on {now}.");
            }
            // Typed text can be a command (a console, the Run box): the run_command check first, off the UI thread.
            if (call.Type == ToolType.DesktopType
                && await Safety.CodeRunGuard.CheckAsync(call.GetArg("text"), context.WorkingDirectory) is { } unsafeText)
                return Fail(call, unsafeText);
            return call.Type switch
            {
                ToolType.DesktopWindows => await Ui(() => Windows(call), ct),
                ToolType.DesktopScreenshot => await ScreenshotAsync(call, context, ct),
                ToolType.DesktopInspect => await Ui(() => Inspect(call, context, ct), ct, 60_000),
                ToolType.DesktopFind => await Ui(() => FindTool(call, context, ct), ct, 60_000),
                ToolType.DesktopAct => await Ui(() => Act(call, context, ct), ct),
                ToolType.DesktopType => await Ui(() => TypeText(call, context, ct), ct, 60_000),
                ToolType.DesktopKeys => await Ui(() => Keys(call, context, ct), ct, 60_000),
                ToolType.DesktopLaunch => await LaunchAsync(call, context, ct),
                ToolType.DesktopWait => await WaitAsync(call, context, ct),
                _ => Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id)),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return Fail(call, "The app did not answer UI Automation in time (it may be busy or hung). Try again, or take a desktop_screenshot to see it."); }
        catch (ElementNotAvailableException) { return Fail(call, Stale(call.GetArg("ref"))); }
        catch (InvalidOperationException ex) when (ex.Message == "stale") { return Fail(call, Stale(call.GetArg("ref"))); }
        catch (InvalidOperationException ex) { return Fail(call, ex.Message); }
        catch (ArgumentException ex) when (ex.Message == "bad_action") { return Fail(call, $"Unknown action \"{call.GetArg("action")}\". Use invoke, select, toggle, expand, collapse, set_value, scroll, focus, click, double_click or right_click."); }
        catch (ArgumentException ex) { return Fail(call, ex.Message); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[desktop] {call.ToolName} failed: {ex}");
            _log?.Warn("Desktop", $"{call.ToolName} failed: {ex.GetType().Name}");
            return Fail(call, $"{call.ToolName} failed ({ex.GetType().Name}). The window may have closed — desktop_windows shows what is open.");
        }
    }

    private static string Stale(string? r) =>
        $"Element {r} is gone (the window changed or closed). Run desktop_inspect or desktop_find again for fresh refs.";

    /// <summary>Run UI Automation / Win32 work on its own per-monitor DPI aware thread (never the UI thread: acting
    /// on CluadeX's own window from its UI thread would deadlock), bounded by a timeout and the run's Stop.</summary>
    private static Task<T> Ui<T>(Func<T> f, CancellationToken ct, int timeoutMs = 20_000) =>
        Task.Factory.StartNew(() =>
        {
            using var dpi = DesktopNative.PerMonitorDpi();
            return f();
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);

    private void Log(string tool, string app, string action, string? element) =>
        _log?.Info("Desktop", $"{tool} {action} app={app}{(element != null ? $" element=\"{DesktopWindows.Trunc(element, 80)}\"" : "")}");

    // ── desktop_windows ──

    private ToolResult Windows(ToolCall call)
    {
        string filter = call.GetArg("filter").Trim();
        Func<string, bool> match = s => true;
        if (filter.Length > 2 && filter.StartsWith('/') && filter.EndsWith('/'))
        {
            try
            {
                var rx = new Regex(filter[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                match = s => DesktopWindows.SafeMatch(rx, s);
            }
            catch (ArgumentException) { match = s => s.Contains(filter, StringComparison.OrdinalIgnoreCase); }
        }
        else if (filter.Length > 0) match = s => s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var all = DesktopWindows.List().Where(w => match(w.Title) || match(w.ProcessName)).ToList();
        var sb = new StringBuilder($"{all.Count} window(s){(filter.Length > 0 ? $" matching \"{filter}\"" : "")}, front first (screen pixels):\n");
        foreach (var w in all.Take(80))
        {
            sb.Append(w.Format());
            if (DesktopSafety.IsCluadeXProcess(w.ProcessId)) sb.Append(" (CluadeX)");
            if (DesktopSafety.IsCredentialWindow(w.ProcessName, w.ClassName, w.Title)) sb.Append(" (credential window — off limits)");
            sb.Append('\n');
        }
        if (all.Count > 80) sb.Append($"(+{all.Count - 80} more — pass filter)\n");
        var r = Ok(call, sb.ToString().TrimEnd());
        r.Summary = $"{all.Count} window(s)";
        return r;
    }

    // ── target resolution ──

    private static DesktopWindow ResolveWindow(ToolCall call, bool foregroundDefault = false)
    {
        var w = DesktopWindows.Resolve(call.GetArg("handle"), call.GetArg("pid"), call.GetArg("title"), foregroundDefault, out var err);
        if (w == null) throw new InvalidOperationException(err ?? "No such window.");
        if (DesktopSafety.CredentialWindowRefusal(w) is { } cr) throw new InvalidOperationException(cr);
        return w;
    }

    private DesktopRef RefOf(ToolCall call, ToolModuleContext context)
    {
        string id = call.GetArg("ref");
        var r = _uia.Lookup(context.SessionKey, id)
                ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(id)
                    ? "Give a ref (e12) from desktop_inspect or desktop_find."
                    : $"Unknown ref {id}. Refs come from desktop_inspect / desktop_find in this chat.");
        if (!DesktopAutomation.IsAlive(r.Element)) throw new InvalidOperationException("stale");
        if (DesktopWindows.Describe(r.Window) is { } w && DesktopSafety.CredentialWindowRefusal(w) is { } cr) throw new InvalidOperationException(cr);
        return r;
    }

    /// <summary>The root a tree call works under: the ref, else the window.</summary>
    private (AutomationElement Root, IntPtr Window, int Pid, string App, string What) RootOf(ToolCall call, ToolModuleContext context)
    {
        if (!string.IsNullOrWhiteSpace(call.GetArg("ref")))
        {
            var r = RefOf(call, context);
            return (r.Element, r.Window, r.ProcessId, r.ProcessName, r.Id);
        }
        var w = ResolveWindow(call);
        return (DesktopAutomation.WindowElement(w.Handle), w.Handle, w.ProcessId, w.ProcessName, $"\"{DesktopWindows.Trunc(w.Title, 80)}\" ({w.ProcessName})");
    }

    // ── desktop_screenshot ──

    private async Task<ToolResult> ScreenshotAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        int maxEdge = Math.Clamp(IntArg(call, "max_edge", ImageCodec.DefaultMaxEdge), 200, 4096);
        var (shot, what, app) = await Ui(() =>
        {
            string region = call.GetArg("region").Trim();
            if (region.Length > 0 && !HasTarget(call))
            {
                var n = region.Split(new[] { ',', ' ', 'x' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? (int?)v : null).ToArray();
                if (n.Length != 4 || n.Any(v => v == null) || n[2] <= 0 || n[3] <= 0)
                    throw new ArgumentException("region is 'x,y,width,height' in screen pixels, e.g. '-1920,0,800,600'.");
                // A region that covers a credential window is still a picture of it.
                foreach (var w in DesktopWindows.List())
                    if (DesktopSafety.IsCredentialWindow(w.ProcessName, w.ClassName, w.Title)
                        && w.X < n[0]!.Value + n[2]!.Value && n[0]!.Value < w.X + w.Width && w.Y < n[1]!.Value + n[3]!.Value && n[1]!.Value < w.Y + w.Height)
                        throw new InvalidOperationException(DesktopSafety.CredentialWindowRefusal(w)!);
                return (DesktopCapture.CaptureRegion(n[0]!.Value, n[1]!.Value, n[2]!.Value, n[3]!.Value), $"screen region {region}", "screen");
            }
            var win = ResolveWindow(call);
            return (DesktopCapture.CaptureWindow(win.Handle), $"window \"{DesktopWindows.Trunc(win.Title, 80)}\" ({win.ProcessName}, {win.HandleText})", win.ProcessName);
        }, ct);
        Log("desktop_screenshot", app, "capture", null);

        string? path = null, saveNote = null;
        try { path = SavePng(shot.Png, context.SessionKey); }
        catch (Exception ex) { saveNote = "not saved: " + ex.Message; }

        var decoded = await ImageCodec.ProcessAsync(shot.Png, "image/png", maxEdge);
        var sb = new StringBuilder();
        sb.Append($"Screenshot of {what}: {shot.Width}x{shot.Height} physical px, top-left at screen ({shot.ScreenX},{shot.ScreenY}) [{shot.Method}].\n");
        if (path != null) sb.Append("PNG saved at ").Append(path).Append('\n');
        var result = Ok(call, "");
        if (decoded.Image is { } img)
        {
            img = img with { FilePath = path, Caption = $"Desktop screenshot of {what}; image (0,0) = screen ({shot.ScreenX},{shot.ScreenY})" };
            result.Images = new List<ToolImage> { img };
            sb.Append(ToolImageWire.AttachedMarker(1, img, shot.Width, shot.Height, saveNote)).Append('\n');
            sb.Append("To act on something you see, prefer desktop_find / desktop_inspect refs over coordinates.");
        }
        else sb.Append("(the image could not be attached: ").Append(decoded.Error).Append(')');
        result.Output = sb.ToString().TrimEnd();
        result.Summary = $"{shot.Width}x{shot.Height} {app}";
        result.FilePath = path;
        return result;
    }

    /// <summary>DataRoot\screens\&lt;chat&gt;\desktop\NNN.png, newest 50 kept.</summary>
    private string SavePng(byte[] png, string session)
    {
        string root = _images != null ? _images.SessionDirectory(session) : Path.Combine(Path.GetTempPath(), "cluadex-desktop", ToolImageStore.SafeSessionName(session));
        string dir = Path.Combine(root, "desktop");
        Directory.CreateDirectory(dir);
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("the screenshot folder is a link");
        var existing = Directory.EnumerateFiles(dir, "*.png").Select(f => (f, n: int.TryParse(Path.GetFileNameWithoutExtension(f), out var v) ? v : 0))
            .Where(x => x.n > 0).OrderByDescending(x => x.n).ToList();
        int next = existing.Count > 0 ? existing[0].n + 1 : 1;
        foreach (var old in existing.Skip(49)) try { File.Delete(old.f); } catch { }
        string path = Path.Combine(dir, next.ToString("D3") + ".png");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(png, 0, png.Length);
        return path;
    }

    // ── desktop_inspect / desktop_find ──

    private ToolResult Inspect(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var (root, win, pid, app, what) = RootOf(call, context);
        int depth = Math.Clamp(IntArg(call, "depth", 6), 0, 30);
        int limit = Math.Clamp(IntArg(call, "limit", 150), 1, 1000);
        int budget = Math.Max(2000, context.OutputBudgetChars - 500);
        string tree = _uia.Inspect(context.SessionKey, root, win, pid, app, depth, limit, budget, ct, out int n, out bool cut);
        Log("desktop_inspect", app, "inspect", null);
        var sb = new StringBuilder($"UI tree of {what} — {n} element(s), depth ≤ {depth}. Bounds are screen pixels.\n");
        sb.Append(tree);
        if (cut) sb.Append($"(cut at {n} elements — inspect under a ref, or raise limit / lower depth)\n");
        var r = Ok(call, sb.ToString().TrimEnd());
        r.Summary = $"{n} element(s) in {app}";
        return r;
    }

    private ToolResult FindTool(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var q = QueryOf(call);
        if (q.IsEmpty) throw new ArgumentException("Give at least one of name, automation_id, control_type, text.");
        var (root, win, pid, app, what) = RootOf(call, context);
        var found = _uia.Find(context.SessionKey, root, win, pid, app, q, Math.Clamp(IntArg(call, "limit", 20), 1, 100), ct);
        Log("desktop_find", app, "find", null);
        string body = found.Count == 0
            ? $"No element matches {q} in {what}. desktop_inspect shows what is there."
            : $"{found.Count} match(es) for {q} in {what}:\n" + string.Join("\n", found.Select(f => DesktopAutomation.Describe(f)));
        var r = Ok(call, body);
        r.Summary = $"{found.Count} match(es)";
        return r;
    }

    private static DesktopAutomation.Query QueryOf(ToolCall call) =>
        new(call.GetArg("name"), call.GetArg("automation_id"), call.GetArg("control_type"), call.GetArg("text"));

    // ── desktop_act ──

    private ToolResult Act(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var r = RefOf(call, context);
        string action = call.GetArg("action").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (action is "setvalue" or "set") action = "set_value";
        if (action is "doubleclick") action = "double_click";
        if (action is "rightclick") action = "right_click";
        if (DesktopSafety.GuardRefusal(r.Element) is { } g) throw new InvalidOperationException(g);
        if (action == "set_value" && DesktopWindows.Describe(r.Window) is { } aw
            && DesktopSafety.CommandTextRefusal(aw, call.GetArg("value")) is { } cmdNo) throw new InvalidOperationException(cmdNo);
        string name = SafeName(r.Element);
        string done = DesktopAutomation.Act(r, action, call.GetArg("value"), call.GetArg("direction"), IntArg(call, "amount", 3), ct);
        Log("desktop_act", r.ProcessName, action, name);
        Thread.Sleep(150);   // let the app react before reading its state back
        var sb = new StringBuilder($"{r.Id} {done}.\n");
        if (DesktopAutomation.IsAlive(r.Element)) sb.Append("Now: ").Append(DesktopAutomation.Describe(r)).Append('\n');
        else sb.Append("The element is gone now (its window changed or closed).\n");
        AppendFocus(sb, context, r.ProcessId);
        var res = Ok(call, sb.ToString().TrimEnd());
        res.Summary = $"{action} {DesktopWindows.Trunc(name, 40)} ({r.ProcessName})";
        return res;
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; } catch { return ""; }
    }

    /// <summary>"Focus: e7 Edit "Message"" — the focused element when it is in the same app.</summary>
    private void AppendFocus(StringBuilder sb, ToolModuleContext context, int pid)
    {
        try
        {
            var f = AutomationElement.FocusedElement;
            if (f == null || f.Current.ProcessId != pid) return;
            var top = DesktopAutomation.TopLevelOf(f) ?? IntPtr.Zero;
            var fr = _uia.Register(context.SessionKey, f, top, pid, DesktopWindows.ProcessNameOf(pid));
            sb.Append("Focus: ").Append(DesktopAutomation.Describe(fr, withValue: false)).Append('\n');
        }
        catch { }
    }

    // ── desktop_type ──

    private ToolResult TypeText(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        string text = call.GetArg("text");
        bool clear = BoolArg(call, "clear"), submit = BoolArg(call, "submit");
        if (text.Length == 0 && !clear && !submit) throw new ArgumentException("Give the text to type.");
        AutomationElement target;
        IntPtr window;
        if (!string.IsNullOrWhiteSpace(call.GetArg("ref")))
        {
            var r = RefOf(call, context);
            if (DesktopSafety.GuardRefusal(r.Element) is { } g0) throw new InvalidOperationException(g0);
            if (DesktopSafety.PasswordRefusal(r.Element) is { } pw0) throw new InvalidOperationException(pw0);
            if (!DesktopWindows.Activate(r.Window)) throw new InvalidOperationException(NotInFront(r.Window));
            try { r.Element.SetFocus(); } catch { }
            target = r.Element;
            window = r.Window;
        }
        else
        {
            target = AutomationElement.FocusedElement ?? throw new InvalidOperationException("Nothing has the keyboard focus. Pass ref.");
            window = DesktopAutomation.TopLevelOf(target) ?? DesktopNative.GetForegroundWindow();
        }
        var win = DesktopWindows.Describe(window) ?? throw new InvalidOperationException("The target window is gone.");
        if (DesktopSafety.CredentialWindowRefusal(win) is { } cr) throw new InvalidOperationException(cr);
        if (DesktopSafety.GuardRefusal(target) is { } g) throw new InvalidOperationException(g);
        if (DesktopSafety.PasswordRefusal(target) is { } pw) throw new InvalidOperationException(pw);
        if (DesktopSafety.CommandTextRefusal(win, text) is { } cmdNo) throw new InvalidOperationException(cmdNo);

        string how;
        if (text.Length > 0 || clear)
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo) && vpo is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                string before = clear ? "" : vp.Current.Value ?? "";
                vp.SetValue(before + text);
                how = "set through the Value pattern";
            }
            else
            {
                if (!DesktopWindows.Activate(window)) throw new InvalidOperationException(NotInFront(window));
                if (clear)
                {
                    DesktopInput.SendChord(DesktopInput.Parse("ctrl+a")!);
                    DesktopInput.SendChord(DesktopInput.Parse("Delete")!);
                }
                // The focus must still be this field when the keys go out (nothing moved it into a password box).
                var now = AutomationElement.FocusedElement;
                if (now != null && DesktopSafety.PasswordRefusal(now) is { } pw2) throw new InvalidOperationException(pw2);
                DesktopInput.SendText(text, ct);
                how = "typed as key input";
            }
        }
        else how = "nothing typed";
        if (submit)
        {
            if (!DesktopWindows.Activate(window)) throw new InvalidOperationException(NotInFront(window));
            if (DesktopSafety.GuardRefusal(AutomationElement.FocusedElement) is { } g2) throw new InvalidOperationException(g2);
            DesktopInput.SendChord(DesktopInput.Parse("Enter")!);
        }
        string name = SafeName(target);
        Log("desktop_type", win.ProcessName, "type", name);
        Thread.Sleep(100);
        var sb = new StringBuilder($"{new System.Globalization.StringInfo(text).LengthInTextElements} character(s) {how} into \"{DesktopWindows.Trunc(name, 60)}\" ({win.ProcessName}){(submit ? ", then Enter" : "")}.\n");
        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var after))
                sb.Append("Value now: \"").Append(DesktopWindows.Trunc(((ValuePattern)after).Current.Value, 200)).Append("\"\n");
        }
        catch { }
        var res = Ok(call, sb.ToString().TrimEnd());
        res.Summary = $"typed into {DesktopWindows.Trunc(name, 40)} ({win.ProcessName})";
        return res;
    }

    private static string NotInFront(IntPtr h) =>
        $"Could not bring the window {DesktopWindows.Describe(h)?.Title ?? ""} to the front, so no keys were sent (Windows refuses focus changes while the user is typing elsewhere). Try again, or click it with desktop_act click.";

    // ── desktop_keys ──

    private ToolResult Keys(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var chords = new List<DesktopChord>();
        foreach (var token in call.GetArg("keys").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = DesktopInput.Parse(token) ?? throw new ArgumentException($"Unknown key \"{token}\". Use names like Enter, Tab, Esc, F5, Up, ctrl+s, alt+F4, shift+Tab.");
            if (c.UsesWin) throw new ArgumentException($"\"{token}\" uses the Windows key, which acts on Windows itself rather than the app — refused.");
            chords.Add(c);
        }
        if (chords.Count == 0) throw new ArgumentException("Give keys, e.g. 'ctrl+s' or 'Tab Enter'.");
        int repeat = Math.Clamp(IntArg(call, "repeat", 1), 1, 50);
        int hold = Math.Clamp(IntArg(call, "hold_ms", 0), 0, 5000);

        IntPtr window;
        if (!string.IsNullOrWhiteSpace(call.GetArg("ref")))
        {
            var r = RefOf(call, context);
            if (DesktopSafety.GuardRefusal(r.Element) is { } g0) throw new InvalidOperationException(g0);
            window = r.Window;
            if (!DesktopWindows.Activate(window)) throw new InvalidOperationException(NotInFront(window));
            try { r.Element.SetFocus(); } catch { }
        }
        else window = ResolveWindow(call, foregroundDefault: true).Handle;
        var win = DesktopWindows.Describe(window) ?? throw new InvalidOperationException("The window is gone.");
        if (DesktopSafety.CredentialWindowRefusal(win) is { } cr) throw new InvalidOperationException(cr);

        int sent = 0;
        for (int i = 0; i < repeat; i++)
            foreach (var c in chords)
            {
                ct.ThrowIfCancellationRequested();
                // Checked before EVERY chord: a Tab can move the focus into a password box or onto an Allow button,
                // and another window can come to the front mid-sequence.
                if (DesktopNative.GetForegroundWindow() != window && !DesktopWindows.Activate(window))
                    throw new InvalidOperationException(NotInFront(window) + $" ({sent} chord(s) were sent before that.)");
                var focus = AutomationElement.FocusedElement;
                if (DesktopSafety.GuardRefusal(focus) is { } g) throw new InvalidOperationException(g + $" ({sent} chord(s) were sent before that.)");
                // Re-read the foreground window each chord: a key can open a console or the Run box mid-sequence.
                var fg = DesktopWindows.Describe(DesktopNative.GetForegroundWindow()) ?? win;
                if (DesktopSafety.CommandKeyRefusal(fg, c) is { } keyNo) throw new InvalidOperationException(keyNo + $" ({sent} chord(s) were sent before that.)");
                if (c.PutsText && focus != null && DesktopSafety.PasswordRefusal(focus) is { } pw) throw new InvalidOperationException(pw);
                DesktopInput.SendChord(c, hold);
                sent++;
                Thread.Sleep(40);
            }
        Log("desktop_keys", win.ProcessName, "keys", string.Join(" ", chords.Select(c => c.PutsText ? "(text key)" : c.Text)));
        Thread.Sleep(120);
        var sb = new StringBuilder($"Pressed {string.Join(", ", chords.Select(c => c.Text))}{(repeat > 1 ? $" ×{repeat}" : "")} in \"{DesktopWindows.Trunc(win.Title, 60)}\" ({win.ProcessName}).\n");
        AppendFocus(sb, context, win.ProcessId);
        var res = Ok(call, sb.ToString().TrimEnd());
        res.Summary = $"{sent} key chord(s) → {win.ProcessName}";
        return res;
    }

    // ── desktop_launch ──

    private async Task<ToolResult> LaunchAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        string path = call.GetArg("path").Trim().Trim('"');
        if (path.Length == 0) throw new ArgumentException("Give the program to start (path).");
        string args = call.GetArg("args");
        string commandLine = (path + " " + args).Trim();
        if (DangerousCommandCheck.FindBlocked(commandLine) is { } blocked)
            return Fail(call, DangerousCommandCheck.BlockedMessage(blocked));
        string? cwd = call.GetArg("cwd").Trim().Trim('"');
        if (string.IsNullOrEmpty(cwd)) cwd = context.WorkingDirectory;
        if (cwd != null && !Directory.Exists(cwd)) throw new ArgumentException($"The working directory {cwd} does not exist.");
        string file = path;
        if (!Path.IsPathRooted(file) && cwd != null && File.Exists(Path.Combine(cwd, file))) file = Path.Combine(cwd, file);
        // Always on, as for run_command: the command line, the code changed in the project when it runs code, and a
        // script handed straight to the shell (a .bat / .ps1 / .py … is code, not an app).
        if (await Safety.CodeRunGuard.CheckAsync(commandLine, cwd) is { } unsafeRun) return Fail(call, unsafeRun);
        if (Safety.CodeRunGuard.CheckScriptFile(file) is { } unsafeScript) return Fail(call, unsafeScript);
        if (Safety.ProcessLaunchPolicy.Blocks(file))
            return Ok(call, Safety.ProcessLaunchPolicy.Report(commandLine));
        bool exe = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(file);
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = !exe, WorkingDirectory = cwd ?? "" };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) { return Fail(call, $"Could not start {path}: {ex.Message}"); }
        string app = Path.GetFileNameWithoutExtension(file);
        Log("desktop_launch", app, "launch", null);
        if (p == null) return Ok(call, $"Started {path} through the shell (no process id to follow). Use desktop_wait with title to wait for its window.");
        int timeout = Math.Clamp(IntArg(call, "timeout_ms", 20_000), 500, 120_000);
        var sw = Stopwatch.StartNew();
        DesktopWindow? win = null;
        while (sw.ElapsedMilliseconds < timeout)
        {
            ct.ThrowIfCancellationRequested();
            win = await Ui(() => DesktopWindows.List().FirstOrDefault(w => w.ProcessId == p.Id), ct);
            if (win != null) break;
            if (p.HasExited) break;
            await Task.Delay(200, ct);
        }
        var sb = new StringBuilder($"Started {path}{(args.Length > 0 ? " " + args : "")} — pid {p.Id}.\n");
        if (win != null) sb.Append($"Window after {sw.ElapsedMilliseconds} ms: ").Append(win.Format()).Append('\n');
        else if (p.HasExited) sb.Append($"The process exited with code {p.ExitCode} before showing a window (a launcher that starts the real app?). Use desktop_windows or desktop_wait title=… to find it.\n");
        else sb.Append($"No window yet after {timeout} ms (still starting, or a console/background program). desktop_wait title=… waits longer.\n");
        var res = Ok(call, sb.ToString().TrimEnd());
        res.Summary = $"{app} pid {p.Id}{(win != null ? "" : " (no window)")}";
        return res;
    }

    // ── desktop_wait ──

    private async Task<ToolResult> WaitAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        bool disappear = call.GetArg("state").Trim().Equals("disappear", StringComparison.OrdinalIgnoreCase)
                         || call.GetArg("state").Trim().Equals("gone", StringComparison.OrdinalIgnoreCase);
        int timeout = Math.Clamp(IntArg(call, "timeout_ms", 10_000), 100, 120_000);
        var q = QueryOf(call);
        bool element = !q.IsEmpty;
        if (!element && !HasTarget(call)) throw new ArgumentException("Give a window (title / pid / handle) and optionally an element (name / automation_id / control_type / text).");
        var sw = Stopwatch.StartNew();
        string? lastSeen = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (present, line) = await Ui(() =>
            {
                var w = DesktopWindows.Resolve(call.GetArg("handle"), call.GetArg("pid"), call.GetArg("title"), false, out _);
                if (w == null) return (false, (string?)null);
                if (DesktopSafety.CredentialWindowRefusal(w) is { } cr) throw new InvalidOperationException(cr);
                if (!element) return (true, w.Format());
                var found = _uia.Find(context.SessionKey, DesktopAutomation.WindowElement(w.Handle), w.Handle, w.ProcessId, w.ProcessName, q, 1, ct);
                return found.Count > 0 ? (true, DesktopAutomation.Describe(found[0])) : (false, (string?)null);
            }, ct, 30_000);
            if (present) lastSeen = line;
            if (present != disappear)
            {
                string what = element ? q.ToString() : $"window {call.GetArg("title")}{call.GetArg("pid")}{call.GetArg("handle")}".Trim();
                var res = Ok(call, disappear
                    ? $"{what} is gone after {sw.ElapsedMilliseconds} ms."
                    : $"{what} appeared after {sw.ElapsedMilliseconds} ms:\n{line}");
                res.Summary = $"{(disappear ? "gone" : "appeared")} in {sw.ElapsedMilliseconds} ms";
                return res;
            }
            if (sw.ElapsedMilliseconds >= timeout)
                return Fail(call, disappear
                    ? $"Still there after {timeout} ms: {lastSeen}"
                    : $"Did not appear within {timeout} ms ({(element ? q.ToString() : "window")}). desktop_screenshot shows what is on screen.");
            await Task.Delay(250, ct);
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static int IntArg(ToolCall call, string key, int def)
    {
        string s = call.GetArg(key).Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (int)v : def;
    }

    private static bool BoolArg(ToolCall call, string key) =>
        call.GetArg(key).Trim().ToLowerInvariant() is "true" or "1" or "yes";

    private static ToolResult Ok(ToolCall call, string output) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = true, Output = output };

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static string L(string key) => LocalizedResources.Instance[key];
}
