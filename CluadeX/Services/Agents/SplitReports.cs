namespace CluadeX.Services.Agents;

/// <summary>
/// What happens to a split chat's report when it ends a turn, and to a message one chat sends another (owner 2026-11-11:
/// "แชทเซสชั่น หลัก กับย่อย สามารถส่งข้อมูลงานหากันได้ … เมื่อแยกย่อยไปทำงานแล้วเสร็จก็กลับไปรายงาน แชทหลัก … ต้องใช้ได้กับทุกโมเดล").
/// Before this, the report reached the parent only while the parent was running or on screen — a parent that sat idle in
/// the sidebar got nothing, and nobody started it again, so the work the split did was never picked up. Now:
/// <list type="bullet">
/// <item>parent working → a note it reads at its next step (and, if it never takes it, the turn after);</item>
/// <item>parent idle, other splits of it still working → the report waits in the parent as a notice row;</item>
/// <item>parent idle, the last split just finished → a turn starts in the parent with every report (on screen or not),
///   unless the owner switched that off, the parent's own last turn was stopped or failed, the split's turn was the
///   owner's own (not the agent's), or the parent already had <see cref="SplitWakeLimit.MaxWakes"/> such turns.</item>
/// </list>
/// Pure — no clock, no UI — so the loop-guard harness checks every branch. It works the same for every model: the notes
/// and the started turns are CluadeX's, not a provider's feature.
/// </summary>
public static class SplitReports
{
    /// <summary>The report as the owner sees it in the parent (a notice row).</summary>
    public const int ShownChars = 400;
    /// <summary>The report as the parent's agent reads it.</summary>
    public const int ModelChars = 4000;
    /// <summary>A split's whole last answer read on demand (close_chat read).</summary>
    public const int ReadChars = 12000;
    /// <summary>A message one chat sends another (message_chat).</summary>
    public const int MessageChars = 8000;
    /// <summary>Finished splits ending close together are told in ONE turn of the parent.</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(2);

    public enum Delivery
    {
        /// <summary>The parent is working: a note it reads at its next step.</summary>
        NoteToRun,
        /// <summary>A turn starts in the parent with the report.</summary>
        Wake,
        /// <summary>A notice row only; the parent's next turn reads it. <see cref="Decision.Why"/> says why no turn starts.</summary>
        Wait,
    }

    /// <summary>Why a report or message waits instead of starting a turn (<see cref="Reason.None"/> when it does not wait).</summary>
    public enum Reason { None, TargetWorking, ChildStopped, OwnerDrove, SiblingsWorking, AutoOff, TargetStopped, TargetFailed, WakeLimit }

    public sealed record Decision(Delivery Delivery, Reason Why);

    /// <param name="parentRunning">The parent has a run going.</param>
    /// <param name="autoContinue">The owner's switch (Settings ▸ SplitAutoContinue).</param>
    /// <param name="parentLastStopped">The parent's last turn was stopped (Stop) — the owner took over.</param>
    /// <param name="parentLastFailed">The parent's last turn ended in an error.</param>
    /// <param name="childAgentDriven">The split's turn was started by an agent (new_chat, message_chat, a wake) — not the owner typing there.</param>
    /// <param name="childStopped">The split's turn was stopped.</param>
    /// <param name="siblingsWorking">Other splits of the parent still working.</param>
    /// <param name="wakeAllowed">The parent is under its wake limit.</param>
    public static Decision Decide(bool parentRunning, bool autoContinue, bool parentLastStopped, bool parentLastFailed,
        bool childAgentDriven, bool childStopped, int siblingsWorking, bool wakeAllowed)
    {
        if (parentRunning) return new(Delivery.NoteToRun, Reason.TargetWorking);
        if (childStopped) return new(Delivery.Wait, Reason.ChildStopped);
        if (!childAgentDriven) return new(Delivery.Wait, Reason.OwnerDrove);
        if (siblingsWorking > 0) return new(Delivery.Wait, Reason.SiblingsWorking);
        if (!autoContinue) return new(Delivery.Wait, Reason.AutoOff);
        if (parentLastStopped) return new(Delivery.Wait, Reason.TargetStopped);
        if (parentLastFailed) return new(Delivery.Wait, Reason.TargetFailed);
        if (!wakeAllowed) return new(Delivery.Wait, Reason.WakeLimit);
        return new(Delivery.Wake, Reason.None);
    }

    /// <summary>A message for another chat: a note to its run, a turn there, or a row it keeps (and why).</summary>
    public static Decision DecideMessage(bool targetRunning, bool targetLastStopped, bool wakeAllowed)
    {
        if (targetRunning) return new(Delivery.NoteToRun, Reason.TargetWorking);
        if (targetLastStopped) return new(Delivery.Wait, Reason.TargetStopped);
        if (!wakeAllowed) return new(Delivery.Wait, Reason.WakeLimit);
        return new(Delivery.Wake, Reason.None);
    }

    /// <summary>How the split's turn ended, as the parent's agent reads it.</summary>
    public static string Outcome(bool stopped, bool failed) => stopped ? "was stopped" : failed ? "ended with an error" : "ended a turn";

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters at a word where it can be, marked when cut.
    /// <paramref name="oneLine"/>: runs of white space become one space (a notice row); otherwise line breaks stay.</summary>
    public static string Clip(string? text, int max, bool oneLine = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string t = oneLine
            ? string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : text.Replace("\r\n", "\n").Trim();
        if (t.Length <= max) return t;
        int cut = t.LastIndexOfAny(new[] { ' ', '\n' }, Math.Max(0, max - 1));
        if (cut < max * 3 / 4) cut = max;
        return t[..cut].TrimEnd() + " …";
    }

    /// <summary>The note the parent's agent reads when a split ends a turn.</summary>
    public static string ReportForModel(string title, string chatId, string chatRef, string outcome, string? answer, string? worktreeNote)
    {
        string body = Clip(answer, ModelChars);
        bool cut = body.Length > 0 && body.Length < Clip(answer, int.MaxValue).Length;
        return $"Split chat \"{title}\" (ref {chatRef}, id {chatId}) {outcome}."
             + (body.Length > 0 ? " Its report:\n" + body : " It gave no answer.")
             + (string.IsNullOrEmpty(worktreeNote) ? "" : "\n" + worktreeNote.Trim())
             + (cut ? $"\n(Cut at {ModelChars} characters: close_chat with chat \"{chatRef}\" and read \"true\" gives the whole answer.)" : "");
    }

    /// <summary>The message a woken parent's turn starts with: the reports are the notices just above it in the chat.</summary>
    public static string WakeForModel(int reports) =>
        (reports > 1 ? $"The {reports} split chats you started have ended their turns" : "The split chat you started has ended its turn")
        + "; their reports are the CluadeX notices just above. Carry on with the user's request from them: use what came back "
        + "(merge a named branch or open a PR from it, check what it says it did), do not redo their work, and tell the user what "
        + "came back and what is left. A split chat asked you something: answer it with message_chat. Put away the ones that are "
        + "done with close_chat.";

    /// <summary>What a split chat's agent is told every turn: who opened it, and that its answer goes back there by itself.</summary>
    public static string SplitInstruction(string parentTitle, string task) =>
        $"This chat was opened by chat \"{parentTitle}\" (another agent) to do: {Clip(task, 300, oneLine: true)}. When you end a "
        + "turn, your final answer goes back to that chat by itself — so end with a complete report it can act on: what you did, "
        + "what you checked and how, what is left, and a branch or PR when there is one. Do not stop to ask that chat for "
        + "permission to start. Only when you cannot go on without its answer (a decision, a missing fact): message_chat with chat "
        + "\"parent\" and end your turn — its answer comes back here as a message.";

    /// <summary>A message between two chats, as the receiving agent reads it. <paramref name="sibling"/>: from another
    /// split chat of the same parent.</summary>
    public static string MessageForModel(bool fromParent, string fromTitle, string fromRef, string fromId, string text, bool sibling = false) =>
        (fromParent
            ? $"Message from the chat that opened you, \"{fromTitle}\" (ref {fromRef}, id {fromId}):\n"
            : sibling
            ? $"Message from \"{fromTitle}\" (ref {fromRef}, id {fromId}), another split chat of the chat that opened you — this is "
              + "work for this chat; answer it with message_chat chat \"{fromRef}\" when it asks something:\n"
            : $"Message from your split chat \"{fromTitle}\" (ref {fromRef}, id {fromId}) — answer it with message_chat chat \"{fromRef}\" when it asks something:\n")
        + Clip(text, MessageChars);

    // ─── which chat a piece of work belongs to (owner 2026-11-11: "ต้องฉลาดพอรู้ว่างานที่คุยมีในแชทย่อยหรือยัง … หรือต้องส่งไป
    //     สมทบกับแชทที่ทำงานได้เองที่เป็นเรื่องเดียวกันคล้ายๆ กัน" — and "อย่าสร้างสิ่งไม่จำเป็นจนทำให้เสียโทเค็น") ───

    /// <summary>A split chat that is not put away, as the router and the parent's note see it.</summary>
    public sealed record OpenSplit(string Id, string Ref, string Title, string Task, bool Working, string? Gist = null, bool Sibling = false);

    /// <summary>At or above this, new_chat treats the task as one an open split chat already has.</summary>
    public const double SameWorkAt = 0.7;
    /// <summary>The parent's note lists this many split chats; the rest are counted (close_chat list has them all).</summary>
    public const int NoteMax = 8;

    /// <summary>Lower case, letters / digits / marks only (Thai keeps its vowels and tone marks), one space between words.</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark)
            { if (space && sb.Length > 0) sb.Append(' '); sb.Append(ch); space = false; }
            else space = true;
        }
        return sb.ToString();
    }

    private static HashSet<string> Grams(string n)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 3 <= n.Length; i++) set.Add(n.Substring(i, 3));
        return set;
    }

    /// <summary>
    /// How much of the shorter task the longer one holds (0–1), by letter triples — works for Thai, which has no spaces
    /// between words, as well as English. Short texts (under 4 triples) count only when they are the same. Pure.
    /// </summary>
    public static double Likeness(string? a, string? b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        var ga = Grams(x); var gb = Grams(y);
        var (small, big) = ga.Count <= gb.Count ? (ga, gb) : (gb, ga);
        if (small.Count < 4) return 0;
        return small.Count(big.Contains) / (double)small.Count;
    }

    /// <summary>The open split chat whose task (or title) is most like <paramref name="task"/>, when it is at least
    /// <see cref="SameWorkAt"/> alike; null otherwise. Pure.</summary>
    public static OpenSplit? SameWork(string? task, IEnumerable<OpenSplit> open)
    {
        OpenSplit? best = null; double top = 0;
        foreach (var s in open)
        {
            double l = Math.Max(Likeness(task, s.Task), Likeness(task, s.Title));
            if (l > top) { top = l; best = s; }
        }
        return top >= SameWorkAt ? best : null;
    }

    /// <summary>What new_chat says when an open split chat already has the task.</summary>
    public static string SameWorkRefusal(OpenSplit hit) =>
        $"Not opened: {(hit.Sibling ? "another split chat of the chat that opened you" : "your split chat")} \"{hit.Title}\" "
        + $"(ref {hit.Ref}) already has this work ({Clip(hit.Task, 120, oneLine: true)}; {(hit.Working ? "working" : "finished, not put away")}). "
        + $"Send it there with message_chat chat \"{hit.Ref}\" — the message joins that chat's work"
        + (hit.Working ? " at its next step" : " and starts a turn there")
        + ". Only when it really is different work, call new_chat again with again \"true\".";

    /// <summary>
    /// The note a chat's turn carries while it has split chats that are not put away (null when it has none — a chat that
    /// never split pays nothing for this): each on one short line, and where new work goes. Pure.
    /// </summary>
    public static string? ChildrenNote(IReadOnlyList<OpenSplit> open)
    {
        if (open.Count == 0) return null;
        var lines = open.Take(NoteMax).Select(s =>
            $"- {s.Ref} \"{Clip(s.Title, 60, oneLine: true)}\""
            + (Likeness(s.Title, s.Task) >= SameWorkAt ? "" : $" — {Clip(s.Task, 80, oneLine: true)}")
            + (s.Working ? " — working" : " — finished" + (string.IsNullOrEmpty(s.Gist) ? "" : ": " + Clip(s.Gist, 100, oneLine: true))));
        string more = open.Count > NoteMax ? $"\n- … and {open.Count - NoteMax} more (close_chat list)" : "";
        return "Split chats of this chat that are not put away (each reports back here by itself):\n" + string.Join("\n", lines) + more
             + "\nFor each new request: if it is the same or close to one of these chats' work, send it there with message_chat "
             + "(do not do it here, do not open another chat); if it builds on this chat's work, do it here; only separate new work "
             + "goes to new_chat. When the user asks how things stand, answer from this list. Put finished ones away with close_chat "
             + "once their result is used.";
    }
}

/// <summary>
/// How often something may start a turn in one chat on its own (a split's report waking its parent, a message starting a
/// chat): <see cref="MaxWakes"/> in <see cref="Window"/>. A parent and a split that keep messaging each other would
/// otherwise run turns without end. Thread-safe; the clock is passed in.
/// </summary>
public sealed class SplitWakeLimit
{
    public int MaxWakes { get; init; } = 8;
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(2);
    private readonly Dictionary<string, List<DateTime>> _taken = new(StringComparer.Ordinal);

    /// <summary>Under the limit: counts one and returns true. At it: false.</summary>
    public bool TryTake(string key, DateTime now)
    {
        lock (_taken)
        {
            if (!_taken.TryGetValue(key, out var list)) _taken[key] = list = new();
            list.RemoveAll(t => t <= now - Window);
            if (list.Count >= MaxWakes) return false;
            list.Add(now);
            return true;
        }
    }

    public bool WouldAllow(string key, DateTime now)
    {
        lock (_taken)
            return !_taken.TryGetValue(key, out var list) || list.Count(t => t > now - Window) < MaxWakes;
    }
}
