using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CluadeX.Services.Desktop;

/// <summary>An element the model may name by ref: the element, the top-level window it lives in, and its app.</summary>
public sealed record DesktopRef(string Id, AutomationElement Element, IntPtr Window, int ProcessId, string ProcessName);

/// <summary>
/// UI Automation for the desktop tools: element refs ("e12"), the tree, search, and the actions. Refs are kept per
/// chat and are stable — the same element (by UIA runtime id) keeps its ref across inspect / find calls. Every
/// member runs synchronously; the tool module calls it on a worker thread that is per-monitor DPI aware.
/// </summary>
public sealed class DesktopAutomation
{
    private const int MaxRefsPerChat = 5000;

    private sealed class RefTable
    {
        public readonly Dictionary<string, DesktopRef> ById = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ByRuntimeId = new(StringComparer.Ordinal);
        public int Next = 1;
    }

    private readonly Dictionary<string, RefTable> _tables = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private RefTable Table(string session)
    {
        lock (_gate)
        {
            if (!_tables.TryGetValue(session, out var t)) _tables[session] = t = new RefTable();
            return t;
        }
    }

    /// <summary>The ref for an element (an existing one when this chat saw it before).</summary>
    public DesktopRef Register(string session, AutomationElement e, IntPtr window, int pid, string processName)
    {
        var t = Table(session);
        string rid = RuntimeKey(e);
        lock (_gate)
        {
            if (rid.Length > 0 && t.ByRuntimeId.TryGetValue(rid, out var known) && t.ById.TryGetValue(known, out var kr))
            {
                var updated = kr with { Element = e };
                t.ById[known] = updated;
                return updated;
            }
            if (t.ById.Count >= MaxRefsPerChat) { t.ById.Clear(); t.ByRuntimeId.Clear(); }
            string id = "e" + t.Next++;
            var r = new DesktopRef(id, e, window, pid, processName);
            t.ById[id] = r;
            if (rid.Length > 0) t.ByRuntimeId[rid] = id;
            return r;
        }
    }

    public DesktopRef? Lookup(string session, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim().TrimStart('[').TrimEnd(']');
        if (!id.StartsWith('e') && int.TryParse(id, out _)) id = "e" + id;
        lock (_gate) return _tables.TryGetValue(session, out var t) && t.ById.TryGetValue(id, out var r) ? r : null;
    }

    private static string RuntimeKey(AutomationElement e)
    {
        try { return string.Join(".", e.GetRuntimeId()); }
        catch { return ""; }
    }

    /// <summary>True when the element still exists (a ref can outlive its element: the dialog closed).</summary>
    public static bool IsAlive(AutomationElement e)
    {
        try { _ = e.Current.ControlType; return true; }
        catch (ElementNotAvailableException) { return false; }
        catch { return false; }
    }

    public static AutomationElement WindowElement(IntPtr hwnd) =>
        AutomationElement.FromHandle(hwnd) ?? throw new InvalidOperationException("The window is not reachable through UI Automation.");

    // ─────────────────────────── describing ───────────────────────────

    /// <summary>"e12 Button "Send" id=SendBtn [x,y wxh] Invoke" — one compact line. Password values never appear.</summary>
    public static string Describe(DesktopRef r, bool withValue = true)
    {
        var e = r.Element;
        var sb = new StringBuilder();
        sb.Append(r.Id).Append(' ');
        try
        {
            var c = e.Current;
            sb.Append(c.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?");
            if (!string.IsNullOrEmpty(c.Name)) sb.Append(" \"").Append(DesktopWindows.Trunc(OneLine(c.Name), 120)).Append('"');
            if (!string.IsNullOrEmpty(c.AutomationId) && !IsNoiseId(c.AutomationId)) sb.Append(" id=").Append(DesktopWindows.Trunc(c.AutomationId, 60));
            if (!string.IsNullOrEmpty(c.HelpText)) sb.Append(" help=\"").Append(DesktopWindows.Trunc(OneLine(c.HelpText), 80)).Append('"');
            var b = c.BoundingRectangle;
            if (!b.IsEmpty && !double.IsInfinity(b.Width)) sb.Append($" [{b.X:0},{b.Y:0} {b.Width:0}x{b.Height:0}]");
            if (!c.IsEnabled) sb.Append(" disabled");
            if (c.IsOffscreen) sb.Append(" offscreen");
            if (c.IsPassword) sb.Append(" password");
            var pats = PatternSummary(e, withValue && !c.IsPassword);
            if (pats.Length > 0) sb.Append(' ').Append(pats);
        }
        catch (ElementNotAvailableException) { sb.Append("(gone)"); }
        return sb.ToString();
    }

    private static bool IsNoiseId(string id) => id.Length > 40 && id.All(ch => char.IsAsciiHexDigit(ch) || ch == '-');

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    /// <summary>The patterns, with their state: Invoke, Toggle(On), Selected, Expanded, Value="…", Range=5[0..10], Scroll.</summary>
    public static string PatternSummary(AutomationElement e, bool withValue)
    {
        var parts = new List<string>();
        AutomationPattern[] supported;
        try { supported = e.GetSupportedPatterns(); }
        catch { return ""; }
        foreach (var p in supported)
        {
            try
            {
                if (p == InvokePattern.Pattern) parts.Add("Invoke");
                else if (p == TogglePattern.Pattern) parts.Add($"Toggle({((TogglePattern)e.GetCurrentPattern(p)).Current.ToggleState})");
                else if (p == SelectionItemPattern.Pattern) parts.Add(((SelectionItemPattern)e.GetCurrentPattern(p)).Current.IsSelected ? "Select(selected)" : "Select");
                else if (p == ExpandCollapsePattern.Pattern) parts.Add($"Expand({((ExpandCollapsePattern)e.GetCurrentPattern(p)).Current.ExpandCollapseState})");
                else if (p == ValuePattern.Pattern)
                {
                    var v = ((ValuePattern)e.GetCurrentPattern(p)).Current;
                    string ro = v.IsReadOnly ? " readonly" : "";
                    parts.Add(withValue ? $"Value=\"{DesktopWindows.Trunc(OneLine(v.Value ?? ""), 120)}\"{ro}" : "Value" + ro);
                }
                else if (p == RangeValuePattern.Pattern)
                {
                    var v = ((RangeValuePattern)e.GetCurrentPattern(p)).Current;
                    parts.Add($"Range={v.Value:0.###}[{v.Minimum:0.###}..{v.Maximum:0.###}]");
                }
                else if (p == ScrollPattern.Pattern) parts.Add("Scroll");
                else if (p == SelectionPattern.Pattern) parts.Add("Selection");
                else if (p == TextPattern.Pattern) parts.Add("Text");
                else if (p == WindowPattern.Pattern) parts.Add("Window");
            }
            catch { }
        }
        return string.Join(",", parts);
    }

    // ─────────────────────────── tree / search ───────────────────────────

    /// <summary>The control-view tree under <paramref name="root"/>, depth- and count-bounded, one line per element.</summary>
    public string Inspect(string session, AutomationElement root, IntPtr window, int pid, string pname, int maxDepth, int maxNodes,
        int maxChars, CancellationToken ct, out int count, out bool truncated)
    {
        var sb = new StringBuilder();
        int n = 0;
        bool cut = false;
        var walker = TreeWalker.ControlViewWalker;
        void Walk(AutomationElement e, int depth)
        {
            if (cut) return;
            ct.ThrowIfCancellationRequested();
            if (n >= maxNodes || sb.Length >= maxChars) { cut = true; return; }
            var r = Register(session, e, window, pid, pname);
            sb.Append(' ', depth * 2).Append(Describe(r)).Append('\n');
            n++;
            if (depth >= maxDepth) return;
            AutomationElement? child;
            try { child = walker.GetFirstChild(e); }
            catch { return; }
            while (child != null && !cut)
            {
                Walk(child, depth + 1);
                try { child = walker.GetNextSibling(child); }
                catch { break; }
            }
        }
        Walk(root, 0);
        count = n;
        truncated = cut;
        return sb.ToString();
    }

    /// <summary>A query over elements. Every given part must match; name / text are case-insensitive substrings or
    /// a /regex/.</summary>
    public sealed record Query(string? Name, string? AutomationId, string? ControlType, string? Text)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(AutomationId)
                               && string.IsNullOrWhiteSpace(ControlType) && string.IsNullOrWhiteSpace(Text);

        public override string ToString() => string.Join(" ", new[]
        {
            Name is { Length: > 0 } ? $"name~\"{Name}\"" : null, AutomationId is { Length: > 0 } ? $"id=\"{AutomationId}\"" : null,
            ControlType is { Length: > 0 } ? $"type={ControlType}" : null, Text is { Length: > 0 } ? $"text~\"{Text}\"" : null,
        }.Where(s => s != null));
    }

    public List<DesktopRef> Find(string session, AutomationElement root, IntPtr window, int pid, string pname, Query q, int limit, CancellationToken ct)
    {
        var name = Matcher(q.Name);
        var text = Matcher(q.Text);
        var id = Matcher(q.AutomationId);
        string? type = string.IsNullOrWhiteSpace(q.ControlType) ? null : q.ControlType.Trim().Replace("ControlType.", "");
        var found = new List<DesktopRef>();
        var walker = TreeWalker.ControlViewWalker;
        int scanned = 0;
        var stack = new Stack<(AutomationElement E, int D)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && found.Count < limit && scanned < 20_000)
        {
            ct.ThrowIfCancellationRequested();
            var (e, d) = stack.Pop();
            scanned++;
            if (d > 0 && Matches(e, name, id, type, text)) found.Add(Register(session, e, window, pid, pname));
            if (d >= 60) continue;
            var kids = new List<AutomationElement>();
            try
            {
                for (var c = walker.GetFirstChild(e); c != null; c = walker.GetNextSibling(c)) kids.Add(c);
            }
            catch { }
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], d + 1));
        }
        return found;
    }

    private static Func<string, bool>? Matcher(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        string p = pattern.Trim();
        if (p.Length > 2 && p.StartsWith('/') && p.EndsWith('/'))
        {
            try
            {
                var rx = new Regex(p[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return s => DesktopWindows.SafeMatch(rx, s);
            }
            catch (ArgumentException) { }
        }
        return s => s.Contains(p, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(AutomationElement e, Func<string, bool>? name, Func<string, bool>? id, string? type, Func<string, bool>? text)
    {
        try
        {
            var c = e.Current;
            if (type != null && !string.Equals(c.ControlType?.ProgrammaticName?.Replace("ControlType.", ""), type, StringComparison.OrdinalIgnoreCase)) return false;
            if (id != null && !id(c.AutomationId ?? "")) return false;
            if (name != null && !name(c.Name ?? "")) return false;
            if (text != null)
            {
                string all = (c.Name ?? "") + "\n" + (c.HelpText ?? "");
                if (!c.IsPassword && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) all += "\n" + ((ValuePattern)vp).Current.Value;
                if (!text(all)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ─────────────────────────── actions ───────────────────────────

    /// <summary>Run one action on an element; returns what was done ("invoked", "selected via child button", …).
    /// Throws InvalidOperationException with a model-facing message when nothing could do it.</summary>
    public static string Act(DesktopRef r, string action, string? value, string? direction, int amount, CancellationToken ct)
    {
        var e = r.Element;
        switch (action)
        {
            case "invoke":
                if (TryPattern<InvokePattern>(e, InvokePattern.Pattern, p => p.Invoke())) return "invoked";
                // A WPF row (DataItem / ListBoxItem) usually has no Invoke itself — its child Button does.
                if (ChildWith(e, InvokePattern.Pattern) is { } child && TryPattern<InvokePattern>(child, InvokePattern.Pattern, p => p.Invoke()))
                    return $"invoked its child {Short(child)}";
                if (TrySelect(e)) return "selected (no Invoke; SelectionItem)";
                if (TryPattern<TogglePattern>(e, TogglePattern.Pattern, p => p.Toggle())) return "toggled (no Invoke)";
                return ClickCentre(r, false, false, ct) + " (no Invoke pattern)";
            case "select":
                if (TrySelect(e)) return "selected";
                if (ChildWith(e, InvokePattern.Pattern) is { } c2 && TryPattern<InvokePattern>(c2, InvokePattern.Pattern, p => p.Invoke()))
                    return $"invoked its child {Short(c2)} (Select did not take)";
                if (TryPattern<InvokePattern>(e, InvokePattern.Pattern, p => p.Invoke())) return "invoked (Select did not take)";
                return ClickCentre(r, false, false, ct) + " (Select did not take)";
            case "toggle":
                if (TryPattern<TogglePattern>(e, TogglePattern.Pattern, p => p.Toggle()))
                    return "toggled → " + (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) ? ((TogglePattern)tp).Current.ToggleState.ToString() : "?");
                return ClickCentre(r, false, false, ct) + " (no Toggle pattern)";
            case "expand":
            case "collapse":
                if (TryPattern<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, p => { if (action == "expand") p.Expand(); else p.Collapse(); }))
                    return action == "expand" ? "expanded" : "collapsed";
                return ClickCentre(r, false, false, ct) + $" (no ExpandCollapse pattern)";
            case "set_value":
                if (DesktopSafety.PasswordRefusal(e) is { } pw) throw new InvalidOperationException(pw);
                value ??= "";
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vpo) && vpo is ValuePattern vp && !vp.Current.IsReadOnly)
                {
                    vp.SetValue(value);
                    return $"value set ({value.Length} chars)";
                }
                if (e.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rpo) && rpo is RangeValuePattern rp
                    && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num))
                {
                    rp.SetValue(num);
                    return $"value set to {rp.Current.Value:0.###}";
                }
                throw new InvalidOperationException("The element has no writable Value (or Range) pattern. Focus it and use desktop_type instead.");
            case "scroll":
            {
                if (string.IsNullOrWhiteSpace(direction))
                {
                    if (TryPattern<ScrollItemPattern>(e, ScrollItemPattern.Pattern, p => p.ScrollIntoView())) return "scrolled into view";
                    direction = "down";
                }
                var target = e;
                for (int i = 0; i < 12 && target != null && !target.TryGetCurrentPattern(ScrollPattern.Pattern, out _); i++)
                    target = TreeWalker.ControlViewWalker.GetParent(target);
                if (target == null || !target.TryGetCurrentPattern(ScrollPattern.Pattern, out var spo))
                    throw new InvalidOperationException("Neither the element nor its parents can scroll (no Scroll pattern).");
                var sp = (ScrollPattern)spo;
                var amt = ScrollAmount.SmallIncrement;
                for (int i = 0; i < Math.Clamp(amount, 1, 50); i++)
                {
                    switch (direction!.Trim().ToLowerInvariant())
                    {
                        case "up": if (sp.Current.VerticallyScrollable) sp.ScrollVertical(ScrollAmount.SmallDecrement); break;
                        case "left": if (sp.Current.HorizontallyScrollable) sp.ScrollHorizontal(ScrollAmount.SmallDecrement); break;
                        case "right": if (sp.Current.HorizontallyScrollable) sp.ScrollHorizontal(amt); break;
                        default: if (sp.Current.VerticallyScrollable) sp.ScrollVertical(amt); break;
                    }
                }
                return $"scrolled {direction} ×{Math.Clamp(amount, 1, 50)} → vertical {sp.Current.VerticalScrollPercent:0.#}%";
            }
            case "focus":
                DesktopWindows.Activate(r.Window);
                e.SetFocus();
                return "focused";
            case "click":
                return ClickCentre(r, false, false, ct);
            case "double_click":
                return ClickCentre(r, false, true, ct);
            case "right_click":
                return ClickCentre(r, true, false, ct);
            default:
                throw new ArgumentException("bad_action");
        }
    }

    private static string Short(AutomationElement e)
    {
        try { return $"{e.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "")} \"{DesktopWindows.Trunc(e.Current.Name, 40)}\""; }
        catch { return "element"; }
    }

    private static bool TrySelect(AutomationElement e)
    {
        if (!e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var o)) return false;
        try
        {
            var p = (SelectionItemPattern)o;
            p.Select();
            return p.Current.IsSelected;
        }
        catch { return false; }
    }

    private static bool TryPattern<T>(AutomationElement e, AutomationPattern pattern, Action<T> act) where T : BasePattern
    {
        if (!e.TryGetCurrentPattern(pattern, out var o)) return false;
        try { act((T)o); return true; }
        catch (ElementNotEnabledException) { throw new InvalidOperationException("The element is disabled."); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The first descendant (3 levels) that supports <paramref name="pattern"/>.</summary>
    private static AutomationElement? ChildWith(AutomationElement e, AutomationPattern pattern)
    {
        var walker = TreeWalker.ControlViewWalker;
        var q = new Queue<(AutomationElement, int)>();
        q.Enqueue((e, 0));
        while (q.Count > 0)
        {
            var (cur, d) = q.Dequeue();
            if (d > 0 && cur.TryGetCurrentPattern(pattern, out _)) return cur;
            if (d >= 3) continue;
            try { for (var c = walker.GetFirstChild(cur); c != null; c = walker.GetNextSibling(c)) q.Enqueue((c, d + 1)); }
            catch { }
        }
        return null;
    }

    /// <summary>A real mouse click at the element's centre (clickable point when it has one). Refused when the
    /// point is covered by CluadeX's approval UI or a credential window.</summary>
    public static string ClickCentre(DesktopRef r, bool right, bool dbl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var e = r.Element;
        System.Windows.Point pt;
        try
        {
            if (!e.TryGetClickablePoint(out pt))
            {
                var b = e.Current.BoundingRectangle;
                if (b.IsEmpty || e.Current.IsOffscreen) throw new InvalidOperationException("The element is off-screen — scroll it into view first (desktop_act scroll).");
                pt = new System.Windows.Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            }
        }
        catch (ElementNotAvailableException) { throw new InvalidOperationException("stale"); }
        DesktopWindows.Activate(r.Window);
        int x = (int)Math.Round(pt.X), y = (int)Math.Round(pt.Y);
        AutomationElement? hit = null;
        try { hit = AutomationElement.FromPoint(pt); } catch { }
        if (DesktopSafety.GuardRefusal(hit) is { } g) throw new InvalidOperationException(g);
        if (hit != null)
        {
            try
            {
                if (TopLevelOf(hit) is { } t && DesktopWindows.Describe(t) is { } tw && DesktopSafety.CredentialWindowRefusal(tw) is { } cr)
                    throw new InvalidOperationException(cr);
            }
            catch (ElementNotAvailableException) { }
        }
        DesktopInput.Click(x, y, right, dbl);
        return $"{(right ? "right-" : dbl ? "double-" : "")}clicked at screen ({x},{y})";
    }

    /// <summary>The top-level window handle that contains <paramref name="e"/>.</summary>
    public static IntPtr? TopLevelOf(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = e, last = null;
        for (int i = 0; cur != null && i < 200; i++)
        {
            if (Automation.Compare(cur, root)) break;
            last = cur;
            try { cur = walker.GetParent(cur); }
            catch { break; }
        }
        if (last == null) return null;
        try { int h = last.Current.NativeWindowHandle; return h != 0 ? new IntPtr(h) : null; }
        catch { return null; }
    }
}
