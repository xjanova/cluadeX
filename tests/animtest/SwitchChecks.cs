using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AnimTest;

/// <summary>
/// What opening a chat from the sidebar costs in the shipping MainWindow, on a real chat store
/// (<c>--db &lt;copy of codex.db&gt; --switch</c>). Every dispatcher operation on the UI thread is timed (Dispatcher.Hooks),
/// so a switch reads as: the click until the chat is on screen, until the UI thread is quiet again, how long the UI
/// thread was busy in that time, and the longest single freeze — and which operations took that time.
///
/// Chats are opened the way the sidebar does (LoadSessionCommand), the longest first. Each is opened twice: cold (its
/// row templates never built for it) and again. After each switch it checks what must not be lost: the list sits at
/// the end, the chat's plan card and next-step box are in the list.
///
/// Always a copy of the store: start-up cleans what it opens, and the chats' project folders should point at throwaway
/// folders (the folder a chat opens is followed, with git and skills read there).
/// </summary>
internal static class SwitchChecks
{
    sealed class Op { public string Name = ""; public double Ms; public DateTime End; }

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<(double start, double end, string name)> Ops = new();
    static int _depth, _loaded, _rowsLoaded;
    static readonly int Iters = int.TryParse(Environment.GetEnvironmentVariable("ROW_ITERS"), out var it) ? it : 5;
    static double _opStart;
    static string _opName = "";
    static FieldInfo _methodField;

    static string NameOf(DispatcherOperation op)
    {
        try
        {
            _methodField ??= typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_methodField?.GetValue(op) is Delegate d)
            {
                var m = d.Method;
                string t = m.DeclaringType?.Name ?? "?";
                // Lambdas: <Outer>b__12_0 inside a compiler class — keep the outer type and method.
                if (t.StartsWith("<>")) t = m.DeclaringType?.DeclaringType?.Name ?? t;
                return $"{t}.{m.Name}";
            }
        }
        catch { }
        return op.Priority.ToString();
    }

    static void Hook(Dispatcher d)
    {
        d.Hooks.OperationStarted += (_, e) =>
        {
            if (_depth++ == 0) { _opStart = Clock.Elapsed.TotalMilliseconds; _opName = NameOf(e.Operation) + "@" + e.Operation.Priority; }
        };
        d.Hooks.OperationCompleted += (_, e) => End();
        d.Hooks.OperationAborted += (_, e) => End();
        static void End()
        {
            if (_depth == 0) return;
            if (--_depth == 0)
            {
                double end = Clock.Elapsed.TotalMilliseconds;
                lock (Ops) Ops.Add((_opStart, end, _opName));
            }
        }
    }

    public static async Task Run(Window win, MainViewModel vm, ServiceProvider sp, string dataRoot)
    {
        var chat = vm.ChatVM;
        var disp = Dispatcher.CurrentDispatcher;
        Hook(disp);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((o, _) =>
        {
            _loaded++;
            if (o is ListBoxItem { DataContext: ChatMessage }) _rowsLoaded++;
        }), true);

        var perfLines = new List<string>();
        sp.GetRequiredService<DebugLogService>().OnEntry += e =>
        {
            if (e.Category is "Perf" or "UI" || e.Message.Contains("UI stall")) lock (perfLines) perfLines.Add($"{e.Category}: {e.Message}");
        };

        // The longest chats in the store.
        var counts = new List<(string id, int n)>();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataRoot, "codex.db")};Mode=ReadOnly"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select session_id, count(*) from messages group by session_id order by 2 desc limit 40";
            using var r = cmd.ExecuteReader();
            while (r.Read()) counts.Add((r.GetString(0), r.GetInt32(1)));
        }
        int take = int.TryParse(Environment.GetEnvironmentVariable("SWITCH_CHATS"), out var tk) ? tk : 8;
        var picks = counts.Where(x => chat.Sessions.Any(s => s.Id == x.id)).Take(take).ToList();
        Console.WriteLine($"INFO store: {chat.Sessions.Count} chats · picked {string.Join(", ", picks.Select(p => p.n))} rows");
        // SWITCH_CACHE="before,after" (pages): try another realized cache around the chat's viewport.
        if (Environment.GetEnvironmentVariable("SWITCH_CACHE") is { Length: > 0 } cache)
            foreach (var l in Find<ListBox>(win).Where(l => l.Name == "ChatList"))
            {
                var parts = cache.Split(',');
                VirtualizingPanel.SetCacheLength(l, new VirtualizationCacheLength(double.Parse(parts[0]), double.Parse(parts[1])));
                VirtualizingPanel.SetCacheLengthUnit(l, VirtualizationCacheLengthUnit.Page);
            }
        chat.ShowAllProjectSessions = true;   // every picked chat has a sidebar row, whichever folder is open
        await Quiet(disp, 1500, 8000);

        // What the idle app costs: subtracted from nothing, just printed so a switch's numbers can be read against it.
        double idleFrom = Now();
        await Task.Delay(2000);
        var idle = Slice(idleFrom, Now());
        Console.WriteLine($"INFO idle 2 s: busy {idle.Sum(o => o.end - o.start):0} ms in {idle.Count} ops, longest {(idle.Count == 0 ? 0 : idle.Max(o => o.end - o.start)):0} ms");

        // SWITCH_HANDLERS=1: time every PropertyChanged subscriber of the chat view model, per property (what a switch's
        // CurrentSession / Messages change sets off). Measurement only: handlers removed later stay in the wrapper.
        var handlerMs = new Dictionary<string, (double ms, int n)>();
        if (Environment.GetEnvironmentVariable("SWITCH_HANDLERS") == "1")
        {
            var field = typeof(ViewModelBase).GetField("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            var original = (System.ComponentModel.PropertyChangedEventHandler)field.GetValue(chat);
            var subs = original.GetInvocationList().Cast<System.ComponentModel.PropertyChangedEventHandler>().ToArray();
            System.ComponentModel.PropertyChangedEventHandler wrap = (s, e) =>
            {
                foreach (var h in subs)
                {
                    var sw = Stopwatch.StartNew();
                    h(s, e);
                    if (e.PropertyName is "CurrentSession" or "Messages")
                    {
                        string key = $"{e.PropertyName} → {h.Method.DeclaringType?.Name}.{h.Method.Name}";
                        handlerMs.TryGetValue(key, out var a);
                        handlerMs[key] = (a.ms + sw.Elapsed.TotalMilliseconds, a.n + 1);
                    }
                }
            };
            field.SetValue(chat, wrap);
        }

        // SWITCH_SPLIT=1: right after the commit (before the layout pass), measure the chat list alone, then the rest of
        // the window: which part of the big layout pass is the chat's rows.
        string split = "";
        if (Environment.GetEnvironmentVariable("SWITCH_SPLIT") == "1")
        {
            chat.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != "Messages") return;
                disp.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    // The top-most elements whose measure is out of date: what the layout pass will redo. Each is
                    // measured alone (with the constraint it had), the costly ones named.
                    var prev = typeof(UIElement).GetProperty("PreviousConstraint", BindingFlags.NonPublic | BindingFlags.Instance);
                    var roots = new List<UIElement>();
                    void Walk(DependencyObject d)
                    {
                        if (d is UIElement u && !u.IsMeasureValid && u.Visibility != Visibility.Collapsed) { roots.Add(u); return; }
                        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
                    }
                    Walk(win);
                    var costs = new List<string>();
                    double total = 0;
                    foreach (var u in roots)
                    {
                        var sw = Stopwatch.StartNew();
                        u.Measure((Size)prev.GetValue(u));
                        double ms = sw.Elapsed.TotalMilliseconds; total += ms;
                        if (ms >= 3) costs.Add($"{Describe(u)} {ms:0}");
                    }
                    var clb = Find<ListBox>(win).FirstOrDefault(l => l.Name == "ChatList" && l.IsVisible);
                    string realizedNow = "";
                    if (clb != null)
                    {
                        var idx = Enumerable.Range(0, clb.Items.Count).Where(i => clb.ItemContainerGenerator.ContainerFromIndex(i) != null).ToList();
                        var csv = Find<ScrollViewer>(clb).FirstOrDefault();
                        realizedNow = $" · first pass realized {idx.Count} rows [{(idx.Count > 0 ? idx.Min() + ".." + idx.Max() : "")}] of {clb.Items.Count} at offset {csv?.VerticalOffset:0}/{csv?.ScrollableHeight:0}";
                    }
                    var sw2 = Stopwatch.StartNew();
                    win.UpdateLayout();
                    split = $"{roots.Count} dirty roots, measured {total:0} ms [{string.Join(", ", costs)}] · then arrange+rest {sw2.Elapsed.TotalMilliseconds:0} ms{realizedNow}";
                });
            };
        }

        var results = new List<(int round, int rows, double commit, double settled, double busy, double longest)>();
        var byName = new Dictionary<string, (double ms, int n)>();
        int fails = 0;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("SWITCH_ROUNDS"), out var rr) ? rr : 2;
        for (int round = 1; round <= rounds; round++)
        {
            foreach (var (id, n) in picks)
            {
                if (chat.CurrentSession?.Id == id) continue;
                lock (perfLines) perfLines.Clear();
                _loaded = 0; _rowsLoaded = 0;
                var lbBefore = Find<ListBox>(win).FirstOrDefault(l => l.Name == "ChatList" && l.IsVisible);
                var oldItems = lbBefore == null ? new HashSet<ListBoxItem>() : Find<ListBoxItem>(lbBefore).ToHashSet();
                double t0 = Now();
                chat.LoadSessionCommand.Execute(id);
                var w = Stopwatch.StartNew();
                while (chat.CurrentSession?.Id != id && w.ElapsedMilliseconds < 15000) await Task.Delay(5);
                double commit = Now() - t0;
                double settledAt = await Quiet(disp, 700, 15000);
                var ops = Slice(t0, settledAt);
                double busy = ops.Sum(o => o.end - o.start);
                var nowItems = lbBefore == null ? new List<ListBoxItem>() : Find<ListBoxItem>(lbBefore).ToList();
                string reuse = $"containers reused {nowItems.Count(oldItems.Contains)}/{nowItems.Count}";
                double longest = ops.Count == 0 ? 0 : ops.Max(o => o.end - o.start);
                results.Add((round, n, commit, settledAt - t0, busy, longest));
                foreach (var o in ops)
                {
                    byName.TryGetValue(o.name, out var acc);
                    byName[o.name] = (acc.ms + (o.end - o.start), acc.n + 1);
                }
                var big = ops.Where(o => o.end - o.start >= 30).Select(o => $"{o.name} {o.end - o.start:0}").ToList();
                string perf;
                lock (perfLines) perf = string.Join(" | ", perfLines);
                Console.WriteLine($"SWITCH r{round} {n,5} rows: on screen @{commit,5:0} · quiet @{settledAt - t0,5:0} · UI busy {busy,5:0} · longest {longest,5:0} ms · rows built {_rowsLoaded} · {reuse} · elements loaded {_loaded} · {Views(chat)}");
                if (big.Count > 0) Console.WriteLine($"         ops ≥30ms: {string.Join(", ", big)}");
                if (Environment.GetEnvironmentVariable("SWITCH_TIMELINE") == "1")
                    Console.WriteLine("         timeline: " + string.Join(" · ", ops.Where(o => o.end - o.start >= 3).Select(o => $"+{o.start - t0:0} {o.name.Split('@')[0].Split('.').Last()} {o.end - o.start:0}")));
                if (perf.Length > 0) Console.WriteLine($"         log: {perf}");
                if (split.Length > 0) { Console.WriteLine($"         split: {split}"); split = ""; }

                // What must not be lost.
                var sv = ChatScroll(win);
                bool atEnd = sv != null && sv.ScrollableHeight - sv.VerticalOffset <= 2;
                if (!atEnd) { fails++; Console.WriteLine($"FAIL  the chat opened at its end ({sv?.VerticalOffset:0} of {sv?.ScrollableHeight:0})"); }
                if (chat.Messages.Count != n) { fails++; Console.WriteLine($"FAIL  every row is on screen ({chat.Messages.Count} of {n})"); }
            }
        }

        await RowCost(win, chat);

        Console.WriteLine();
        foreach (var round in results.GroupBy(r => r.round))
        {
            var g = round.ToList();
            Console.WriteLine($"ROUND {round.Key}: on screen median {Median(g.Select(x => x.commit)):0} · quiet median {Median(g.Select(x => x.settled)):0} · UI busy median {Median(g.Select(x => x.busy)):0} (sum {g.Sum(x => x.busy):0}) · longest median {Median(g.Select(x => x.longest)):0} max {g.Max(x => x.longest):0} ms");
        }
        if (handlerMs.Count > 0)
        {
            Console.WriteLine("PropertyChanged subscribers (all switches):");
            foreach (var kv in handlerMs.OrderByDescending(k => k.Value.ms).Take(15))
                Console.WriteLine($"  {kv.Value.ms,7:0.0} ms  {kv.Value.n,4}×  {kv.Key}");
        }
        Console.WriteLine("TOP UI-thread operations over all switches:");
        foreach (var kv in byName.OrderByDescending(k => k.Value.ms).Take(18))
            Console.WriteLine($"  {kv.Value.ms,7:0} ms  {kv.Value.n,5}×  {kv.Key}");
        Console.WriteLine(fails == 0 ? "\nPASS  every switch ended at the chat's end with all its rows" : $"\n{fails} FAIL");
    }

    /// <summary>One row of each kind, built alone from the chat list's own row template: how many elements it makes and how
    /// long building + measuring it takes. This is the cost each realized row pays on a switch.</summary>
    static async Task RowCost(Window win, ChatViewModel chat)
    {
        var list = Find<ListBox>(win).FirstOrDefault(l => l.Name == "ChatList" && l.IsVisible);
        if (list == null) return;
        // The realized rows on screen now: elements per row (collapsed parts included — they are built all the same).
        var realized = Find<ListBoxItem>(list).ToList();
        foreach (var item in realized.Take(10))
            if (item.DataContext is ChatMessage m)
                Console.WriteLine($"ROW on screen {m.Role,-16} {Count(item),5} elements · {Visible(item),4} visible");

        var host = new StackPanel { Width = 900 };
        var probe = new Border { Child = host, Width = 900 };
        // Rows of each role, from the chat on screen.
        var samples = chat.Messages.GroupBy(m => m.Role).Select(g => g.Last()).ToList();
        var panelParent = VisualTreeHelper.GetParent(list) as Panel;
        foreach (var m in samples)
        {
            double best = double.MaxValue, remeasure = double.MaxValue; int elems = 0;
            for (int i = 0; i < Iters; i++)
            {
                var item = new ListBoxItem { Content = m, ContentTemplate = list.ItemTemplate, Style = list.ItemContainerStyle, DataContext = m };
                var sw = Stopwatch.StartNew();
                host.Children.Clear();
                host.Children.Add(item);
                // Built inside the chat list's own resources and DataContext chain: host it under the list's parent.
                if (probe.Parent == null && panelParent != null) { probe.Visibility = Visibility.Hidden; panelParent.Children.Add(probe); }
                probe.UpdateLayout();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                elems = Count(item);
                // The same row measured again from scratch, nothing built: what text layout alone costs.
                Invalidate(item);
                sw.Restart();
                probe.UpdateLayout();
                remeasure = Math.Min(remeasure, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"ROW alone  {m.Role,-16} build+measure best {best,6:0.0} ms · re-measure only {remeasure,5:0.0} ms · {elems,5} elements · {Visible(host),4} visible");
        }
        // The same row in three steps: the template's objects made (LoadContent), joined to the tree with its message
        // (bindings, styles, triggers, resources resolved), then measured.
        foreach (var m in samples)
        {
            double make = 1e9, join = 1e9, measure = 1e9;
            for (int i = 0; i < Iters; i++)
            {
                var sw = Stopwatch.StartNew();
                var root = (FrameworkElement)list.ItemTemplate.LoadContent();
                double t1 = sw.Elapsed.TotalMilliseconds;
                host.Children.Clear();
                root.DataContext = m;
                host.Children.Add(root);
                // Bindings and styles settle when the element joins a tree: force them without measuring.
                root.ApplyTemplate();
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                double t2 = sw.Elapsed.TotalMilliseconds;
                probe.UpdateLayout();
                double t3 = sw.Elapsed.TotalMilliseconds;
                make = Math.Min(make, t1); join = Math.Min(join, t2 - t1); measure = Math.Min(measure, t3 - t2);
            }
            Console.WriteLine($"ROW steps  {m.Role,-16} make {make,5:0.0} · join {join,5:0.0} · first measure {measure,5:0.0} ms");
        }
        if (probe.Parent is Panel p) p.Children.Remove(probe);
        await Task.Yield();
    }

    static void Invalidate(DependencyObject root)
    {
        if (root is UIElement u) { u.InvalidateMeasure(); u.InvalidateArrange(); }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Invalidate(VisualTreeHelper.GetChild(root, i));
    }

    static int Count(DependencyObject root)
    {
        int n = 1;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) n += Count(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    static int Visible(DependencyObject root)
    {
        if (root is UIElement u && u.Visibility != Visibility.Visible) return 0;
        int n = 1;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) n += Visible(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    static string Describe(UIElement u)
    {
        var chain = new List<string>();
        DependencyObject d = u;
        for (int i = 0; d != null && i < 4; i++)
        {
            string n = d is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $"{d.GetType().Name}#{fe.Name}" : d.GetType().Name;
            chain.Add(n);
            d = VisualTreeHelper.GetParent(d);
        }
        return string.Join("<", chain);
    }

    static string Views(ChatViewModel chat) => CluadeX.Views.ChatView.RealizedRows();

    static double Now() => Clock.Elapsed.TotalMilliseconds;

    static List<(double start, double end, string name)> Slice(double from, double to)
    {
        lock (Ops) return Ops.Where(o => o.end > from && o.start < to).ToList();
    }

    /// <summary>Wait until no UI operation of 4 ms or more has run for <paramref name="gapMs"/>; returns when the last one ended.</summary>
    static async Task<double> Quiet(Dispatcher d, int gapMs, int maxMs)
    {
        double start = Now();
        while (Now() - start < maxMs)
        {
            await Task.Delay(50);
            double last;
            lock (Ops) last = Ops.Where(o => o.end - o.start >= 4 && o.end >= start).Select(o => o.end).DefaultIfEmpty(start).Max();
            if (Now() - last >= gapMs) return last;
        }
        return Now();
    }

    static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    static ScrollViewer ChatScroll(Window win)
    {
        foreach (var lb in Find<ListBox>(win))
            if (lb.Name == "ChatList" && lb.IsVisible)
                return Find<ScrollViewer>(lb).FirstOrDefault();
        return null;
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }
}
