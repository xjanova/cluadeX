using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Safety;

/// <summary>Who answered a strict-approval card. There is deliberately no value for "the model", "tool output" or "a
/// message": the only ways in are the card's own text box and button, and the owner's terminal.</summary>
public enum StrictSource { Card, Terminal }

public enum StrictState { Pending, Approved, Consumed, Denied, Expired, Locked }

public enum StrictAnswer { Approved, WrongCode, Expired, AlreadyAnswered, Locked, NotFound }

/// <summary>One hard-to-undo command waiting for the owner: which tool, the exact command, where it runs (this PC and
/// its folder, or the remote host), the classifier's finding, and the red prompt's finding when there is one.</summary>
public sealed record StrictRequest(string Tool, string Command, string Where, HardToUndoFinding Finding, string? Danger = null);

/// <summary>
/// One strict approval: a one-time code bound to exactly one command. Read by the card (ChatMessage), answered only
/// through <see cref="StrictApprovalLedger"/>.
/// </summary>
public sealed class StrictTicket
{
    internal StrictTicket(string id, string code, string hash, StrictRequest request, DateTime created, DateTime expires)
    {
        Id = id; Code = code; Hash = hash; Request = request; Created = created; ExpiresAt = expires;
    }

    public string Id { get; }
    /// <summary>The code the owner types (shown on the card only — never in a tool result, a log or a prompt).</summary>
    public string Code { get; }
    internal string Hash { get; }
    public StrictRequest Request { get; }
    public DateTime Created { get; }
    public DateTime ExpiresAt { get; }
    public StrictState State { get; internal set; }
    public StrictSource? AnsweredBy { get; internal set; }
    public int WrongTries { get; internal set; }

    /// <summary>Raised (on whatever thread answered) whenever <see cref="State"/> changes.</summary>
    public event Action<StrictTicket>? Changed;
    internal void RaiseChanged() { try { Changed?.Invoke(this); } catch { } }

    /// <summary>The code as the card shows it: ABC-DEF.</summary>
    public string DisplayCode => Code.Length == 6 ? Code[..3] + "-" + Code[3..] : Code;
}

/// <summary>
/// The approval state machine. Pending → Approved (the right code, or "yes", typed into the card; the code in the owner's
/// terminal) → Consumed (the one command it was issued for runs). Pending → Denied / Expired / Locked (too many wrong
/// codes). Nothing goes back: an approval covers one command once; a replay, another command, an expired or a used
/// code is refused. The clock and the code source are injectable (tests pin them); every counter belongs to its own
/// instance, so a harness's ledger never touches the running app's (the self-test-vs-real-attempt rule).
/// </summary>
public sealed class StrictApprovalLedger
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(4);   // < the guard pipe's 280 s answer window
    public const int MaxWrongTries = 5;
    /// <summary>Wrong codes from a terminal in a row before every pending ticket is locked.</summary>
    public const int MaxTerminalWrong = 5;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I/L
    private const int MaxTickets = 64;

    private readonly Func<DateTime> _now;
    private readonly TimeSpan _ttl;
    private readonly Func<int, int> _random;
    private readonly object _lock = new();
    private readonly Dictionary<string, StrictTicket> _tickets = new(StringComparer.Ordinal);
    private int _terminalWrong;

    public StrictApprovalLedger(Func<DateTime>? now = null, TimeSpan? ttl = null, Func<int, int>? random = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _ttl = ttl ?? DefaultTtl;
        _random = random ?? RandomNumberGenerator.GetInt32;
    }

    public TimeSpan Ttl => _ttl;

    /// <summary>What binds a ticket to its command: tool, place and the exact command text.</summary>
    public static string HashOf(StrictRequest r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Tool + "\n" + r.Where + "\n" + r.Command)));

    public StrictTicket Issue(StrictRequest request)
    {
        lock (_lock)
        {
            Sweep();
            string code;
            do
            {
                var sb = new StringBuilder(6);
                for (int i = 0; i < 6; i++) sb.Append(Alphabet[_random(Alphabet.Length)]);
                code = sb.ToString();
            } while (_tickets.Values.Any(t => t.State == StrictState.Pending && t.Code == code));
            var now = _now();
            var t = new StrictTicket(Guid.NewGuid().ToString("N"), code, HashOf(request), request, now, now + _ttl);
            _tickets[t.Id] = t;
            return t;
        }
    }

    public StrictState StateOf(string id)
    {
        StrictTicket? t;
        lock (_lock) { if (!_tickets.TryGetValue(id, out t)) return StrictState.Denied; Roll(t); }
        return t.State;
    }

    /// <summary>The card's answer: the code (any case, with or without the dash and spaces) or "yes".</summary>
    public StrictAnswer Approve(string ticketId, string? typed, StrictSource source)
    {
        StrictTicket? t;
        StrictAnswer answer;
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticketId, out t)) return StrictAnswer.NotFound;
            Roll(t);
            answer = t.State switch
            {
                StrictState.Expired => StrictAnswer.Expired,
                StrictState.Locked => StrictAnswer.Locked,
                StrictState.Pending => StrictAnswer.WrongCode,
                _ => StrictAnswer.AlreadyAnswered,
            };
            if (t.State != StrictState.Pending) return answer;
            string norm = Normalize(typed);
            bool yes = source == StrictSource.Card && norm == "YES";
            if (yes || CodeEquals(norm, t.Code))
            {
                t.State = StrictState.Approved;
                t.AnsweredBy = source;
                answer = StrictAnswer.Approved;
            }
            else if (++t.WrongTries >= MaxWrongTries)
            {
                t.State = StrictState.Locked;
                answer = StrictAnswer.Locked;
            }
            else return StrictAnswer.WrongCode;
        }
        t.RaiseChanged();
        return answer;
    }

    /// <summary>The owner's terminal (<c>CluadeX.exe --cx-approve CODE</c>): the code alone picks the ticket; "yes"
    /// does not count here. <paramref name="ticket"/> = the ticket it approved.</summary>
    public StrictAnswer ApproveByCode(string? code, out StrictTicket? ticket)
    {
        ticket = null;
        string norm = Normalize(code);
        var locked = new List<StrictTicket>();
        StrictAnswer answer;
        lock (_lock)
        {
            Sweep();
            foreach (var t in _tickets.Values) Roll(t);
            // Compare against every pending code (fixed-time each) — no early exit that times the right one.
            StrictTicket? hit = null;
            foreach (var t in _tickets.Values)
                if (t.State == StrictState.Pending & CodeEquals(norm, t.Code)) hit = t;
            if (hit != null)
            {
                _terminalWrong = 0;
                hit.State = StrictState.Approved;
                hit.AnsweredBy = StrictSource.Terminal;
                ticket = hit;
                answer = StrictAnswer.Approved;
            }
            else
            {
                answer = StrictAnswer.WrongCode;
                if (++_terminalWrong >= MaxTerminalWrong)
                {
                    foreach (var t in _tickets.Values.Where(t => t.State == StrictState.Pending))
                    { t.State = StrictState.Locked; locked.Add(t); }
                    _terminalWrong = 0;
                    answer = StrictAnswer.Locked;
                }
            }
        }
        ticket?.RaiseChanged();
        foreach (var t in locked) t.RaiseChanged();
        return answer;
    }

    /// <summary>The look-up a terminal makes before it approves (it shows the owner what the code is for). Null = no
    /// pending ticket has that code (counted like a wrong code).</summary>
    public StrictRequest? Peek(string? code)
    {
        string norm = Normalize(code);
        lock (_lock)
        {
            foreach (var t in _tickets.Values) Roll(t);
            StrictTicket? hit = null;
            foreach (var t in _tickets.Values)
                if (t.State == StrictState.Pending & CodeEquals(norm, t.Code)) hit = t;
            return hit?.Request;
        }
    }

    public bool Deny(string ticketId)
    {
        StrictTicket? t;
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticketId, out t)) return false;
            Roll(t);
            if (t.State != StrictState.Pending) return false;
            t.State = StrictState.Denied;
        }
        t.RaiseChanged();
        return true;
    }

    /// <summary>The command it was approved for is about to run: true exactly once, for exactly that command, before
    /// the ticket expires. Anything else — not approved, already used, another command, too late — is false.</summary>
    public bool Consume(string ticketId, StrictRequest request)
    {
        StrictTicket? t;
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticketId, out t)) return false;
            Roll(t);
            if (t.State != StrictState.Approved) return false;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t.Hash), Encoding.ASCII.GetBytes(HashOf(request))))
                return false;
            t.State = StrictState.Consumed;
        }
        t.RaiseChanged();
        return true;
    }

    /// <summary>Waits until the ticket is answered or expires. True = approved (still to be consumed).</summary>
    public async Task<bool> WaitAsync(StrictTicket t, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(StrictTicket x)
        {
            if (x.State == StrictState.Approved) tcs.TrySetResult(true);
            else if (x.State != StrictState.Pending) tcs.TrySetResult(false);
        }
        t.Changed += OnChanged;
        try
        {
            OnChanged(t);
            var left = t.ExpiresAt - _now();
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(left + TimeSpan.FromMilliseconds(50), cts.Token);
            var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            cts.Cancel();
            if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested) { Deny(t.Id); return false; }
            Expire(t);
            return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
        }
        finally { t.Changed -= OnChanged; }
    }

    private void Expire(StrictTicket t)
    {
        bool changed;
        lock (_lock) { var before = t.State; Roll(t, force: true); changed = before != t.State; }
        if (changed) t.RaiseChanged();
    }

    /// <summary>Pending or approved-but-unused past its time = expired.</summary>
    private void Roll(StrictTicket t, bool force = false)
    {
        if (t.State is StrictState.Pending or StrictState.Approved && (force || _now() > t.ExpiresAt))
            t.State = StrictState.Expired;
    }

    private void Sweep()
    {
        if (_tickets.Count < MaxTickets) return;
        foreach (var old in _tickets.Values.Where(t => t.State is not (StrictState.Pending or StrictState.Approved))
                     .OrderBy(t => t.Created).Take(_tickets.Count - MaxTickets / 2).ToList())
            _tickets.Remove(old.Id);
    }

    private static string Normalize(string? s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    private static bool CodeEquals(string typed, string code)
    {
        var a = Encoding.ASCII.GetBytes(typed.Length == code.Length ? typed : new string('\0', code.Length));
        var b = Encoding.ASCII.GetBytes(code);
        return CryptographicOperations.FixedTimeEquals(a, b) & typed.Length == code.Length;
    }
}

/// <summary>
/// Strict approval (owner 2026-11-11, switchable in Features): a hard-to-undo command (<see cref="HardToUndo"/>) runs only
/// after the owner types its one-time code — or "yes" — into the card in the chat, or approves the code from their own
/// terminal. No rule, no "allow everything", no "Allow in this chat" and no text the model or a tool writes answers it.
/// Off (the default) = everything as before: the normal prompt, and the red prompt for what DestructiveCodeScan finds.
///
/// The switch travels in the process environment (<see cref="EnvName"/>): the app sets it from its settings, and the
/// CLIs it starts pass it on to their guard hooks, so CluadeX and every hook read the same value.
/// </summary>
public static class StrictApproval
{
    /// <summary>"local,remote,git" — the scopes switched on; empty or unset = off.</summary>
    public const string EnvName = "CLUADEX_STRICT_APPROVAL";

    /// <summary>The app's ledger. Tests make their own.</summary>
    public static StrictApprovalLedger Ledger { get; } = new();

    /// <summary>Shows the card for a ticket (ChatViewModel) and returns when the card is gone: false = the user
    /// dismissed it, pressed Stop or the run gave up. The ledger, not this task, says whether it was approved.
    /// Null = nobody can be asked → refused.</summary>
    public static Func<StrictTicket, Task<bool>>? Handler { get; set; }

    /// <summary>Shows BrainX's own approval card (its needs_confirmation answer). True = the owner says they approved it
    /// in BrainX's terminal: CluadeX resends the identical call. Null = no UI → the model gets the redacted answer.</summary>
    public static Func<BrainxConfirmation, Task<bool>>? BrainxHandler { get; set; }

    /// <summary>Set by the native permission stage when it skipped the ordinary prompt because this card will ask: the
    /// gate then asks even if the switch went off in between (a skipped prompt is never a free pass).</summary>
    public static readonly AsyncLocal<bool> Owed = new();

    public static StrictScope Scopes => HardToUndo.ParseScopes(Environment.GetEnvironmentVariable(EnvName));

    public static bool Enabled => Scopes != StrictScope.None;

    /// <summary>Called by the app whenever its settings change. Children started from now on carry the value.</summary>
    public static void Publish(bool enabled, string? scopes)
    {
        var s = enabled ? HardToUndo.ParseScopes(scopes) : StrictScope.None;
        if (enabled && s == StrictScope.None) s = StrictScope.All;
        Environment.SetEnvironmentVariable(EnvName, s == StrictScope.None ? null : HardToUndo.FormatScopes(s));
    }

    /// <summary>The finding strict approval asks about for this command, or null (off, or not hard to undo, or in a
    /// scope that is switched off).</summary>
    public static HardToUndoFinding? Applies(string? command, string? remoteHost = null, StrictScope? scopes = null)
    {
        var on = scopes ?? Scopes;
        if (on == StrictScope.None) return null;
        var f = HardToUndo.Classify(command, remoteHost);
        return f != null && on.HasFlag(f.Scope) ? f : null;
    }

    /// <summary>
    /// The gate a CluadeX runner calls before a shell command: an agent approving its own approval is refused always; with
    /// strict approval on and a hard-to-undo command in a scope that is on, the strict card asks (the red prompt's
    /// finding shown on it, one question); otherwise exactly the red prompt as before. Null = may run.
    /// </summary>
    public static async Task<string?> GateCommandAsync(string tool, string command, string? cwd, string? dangerFinding,
        CancellationToken ct = default)
    {
        if (HardToUndo.IsSelfApproval(command)) return HardToUndo.SelfApprovalRefusal;
        if (Applies(command, scopes: Owed.Value ? StrictScope.All : null) is { } finding)
        {
            string where = finding.Host != null ? "remote: " + finding.Host : "local: " + (cwd ?? Environment.CurrentDirectory);
            return await ConfirmAsync(new StrictRequest(tool, command, where, finding, dangerFinding), ct).ConfigureAwait(false)
                ? null
                : RefusalFor(finding);
        }
        return await DangerApproval.GateAsync(tool, command, dangerFinding).ConfigureAwait(false);
    }

    /// <summary>What the agent is told when it was not approved. Never carries the code.</summary>
    public static string RefusalFor(HardToUndoFinding f) =>
        $"CluadeX strict approval: not approved — this command is hard to undo ({f.Kind}: {f.Matched}). It runs only after the "
        + "owner types its one-time code on CluadeX's card or approves it from their own terminal, and the code was not entered "
        + "(declined, expired or Stop). Do not retry, split or rephrase it, and never try to approve it yourself. Tell the user "
        + "exactly what you wanted to run, what it would delete or change, and what it would not touch.";

    /// <summary>Puts the request to the owner and waits. True only when the ledger says approved AND the approval is
    /// consumed for exactly this request.</summary>
    public static Task<bool> ConfirmAsync(StrictRequest request, CancellationToken ct = default) =>
        ConfirmAsync(Ledger, Handler, request, ct);

    /// <summary>The same with a given ledger and card (the harness drives this).</summary>
    public static async Task<bool> ConfirmAsync(StrictApprovalLedger ledger, Func<StrictTicket, Task<bool>>? handler,
        StrictRequest request, CancellationToken ct = default)
    {
        if (handler == null) return false;
        var ticket = ledger.Issue(request);
        Task<bool> card;
        try { card = handler(ticket); }
        catch { ledger.Deny(ticket.Id); return false; }
        var wait = ledger.WaitAsync(ticket, ct);
        var first = await Task.WhenAny(card, wait).ConfigureAwait(false);
        // The card went away first (dismissed, Stop, the run gave up): whatever it says, nothing was typed — deny,
        // unless a terminal approval landed in the same instant (the ledger is the only authority).
        if (first == card && ledger.StateOf(ticket.Id) == StrictState.Pending) ledger.Deny(ticket.Id);
        bool approved = await wait.ConfigureAwait(false);
        return approved && ledger.Consume(ticket.Id, request);
    }

    // ─── The owner's terminal: CluadeX.exe --cx-approve CODE ───

    public const string ApproveFlag = "--cx-approve";
    private const string PipePrefix = "cluadex-strict-approve-";
    private static int _serverStarted;

    /// <summary>What to copy for the owner's own terminal (the exe this app runs from).</summary>
    public static string TerminalCommand(string displayCode) => $"\"{GuardHook.ExePath()}\" {ApproveFlag} {displayCode}";

    /// <summary>The app's side: one pipe per running CluadeX, current user only.</summary>
    public static void StartApproveServer() => StartApproveServer(Ledger, PipePrefix + Environment.ProcessId);

    internal static void StartApproveServer(StrictApprovalLedger ledger, string name)
    {
        if (Interlocked.Exchange(ref _serverStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch { return; }
                try { await server.WaitForConnectionAsync().ConfigureAwait(false); }
                catch { server.Dispose(); await Task.Delay(500).ConfigureAwait(false); continue; }
                _ = Task.Run(() => ServeApproveAsync(ledger, server));
            }
        });
    }

    private static async Task ServeApproveAsync(StrictApprovalLedger ledger, NamedPipeServerStream server)
    {
        using (server)
        {
            string reply = "error";
            try
            {
                var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                var lineTask = reader.ReadLineAsync();
                if (await Task.WhenAny(lineTask, Task.Delay(5000)).ConfigureAwait(false) == lineTask && lineTask.Result is { Length: > 0 and < 512 } line)
                    // An agent's shell can drop the env the CLI checks; the pipe's other end cannot hide whose child it is.
                    reply = ClientIsOurDescendant(server) ? "refused" : AnswerTerminal(ledger, line);
            }
            catch { reply = "error"; }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(reply.Replace("\n", "\\n") + "\n");
                await server.WriteAsync(bytes).ConfigureAwait(false);
                await server.FlushAsync().ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
            catch { }
        }
    }

    /// <summary>True when the process on the other end of the pipe was started — directly or further down — by this
    /// CluadeX (an agent's shell, a CLI, a hook). Unknown (no pid, no snapshot) counts as ours: refuse rather than guess.</summary>
    private static bool ClientIsOurDescendant(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint client) || client == 0) return true;
            return IsDescendantOf((int)client, Environment.ProcessId);
        }
        catch { return true; }
    }

    /// <summary>Walks the parent chain of <paramref name="pid"/>. A parent that started after its child is a reused id and
    /// ends the walk. No snapshot = true (refuse). Public for the harness.</summary>
    public static bool IsDescendantOf(int pid, int ancestor)
    {
        var parents = ParentMap();
        if (parents == null) return true;
        int cur = pid;
        for (int depth = 0; depth < 64; depth++)
        {
            if (cur == ancestor) return true;
            if (!parents.TryGetValue(cur, out int parent) || parent == 0 || parent == cur) return false;
            if (StartedAfter(parent, cur)) return false;
            cur = parent;
        }
        return false;
    }

    private static bool StartedAfter(int parent, int child)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(parent);
            using var c = System.Diagnostics.Process.GetProcessById(child);
            return p.StartTime > c.StartTime;
        }
        catch { return false; }
    }

    private static Dictionary<int, int>? ParentMap()
    {
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
        try
        {
            var map = new Dictionary<int, int>();
            var e = new ProcessEntry32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref e)) return null;
            do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; while (Process32Next(snap, ref e));
            return map;
        }
        finally { CloseHandle(snap); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>One terminal request: {"op":"peek"|"approve","code":"…"}. Pure apart from the ledger. Public for the
    /// harness.</summary>
    public static string AnswerTerminal(StrictApprovalLedger ledger, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string op = root.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
            string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            if (op == "peek")
                return ledger.Peek(code) is { } r ? "pending\t" + r.Tool + "\t" + r.Where + "\t" + r.Command : "none";
            if (op == "approve")
                return ledger.ApproveByCode(code, out _) switch
                {
                    StrictAnswer.Approved => "approved",
                    StrictAnswer.Locked => "locked",
                    _ => "none",
                };
        }
        catch (JsonException) { }
        return "error";
    }

    /// <summary>The terminal side. A WinExe has no console of its own: it attaches to the terminal it was started from.
    /// Refused from a process CluadeX started (an agent's shell inherits CluadeX's environment) — only the owner's own
    /// terminal approves.</summary>
    public static int RunApproveCli(string[] args)
    {
        AttachParentConsole();
        string code = args.Length > 1 ? string.Join("", args.Skip(1)) : "";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLUADEX_DANGER_PIPE"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvName)))
        {
            Console.Error.WriteLine("CluadeX: refused — this terminal was started by CluadeX (an agent's shell). Approve from your own terminal, or type the code on the card.");
            return 3;
        }
        if (code.Length == 0) { Console.Error.WriteLine("Usage: CluadeX.exe --cx-approve <code>"); return 2; }
        foreach (string pipe in AppPipes())
        {
            string peek = Ask(pipe, JsonSerializer.Serialize(new { op = "peek", code }));
            if (peek == "refused")
            {
                Console.Error.WriteLine("CluadeX: refused — this terminal was started by CluadeX (an agent's shell). Approve from your own terminal, or type the code on the card.");
                return 3;
            }
            if (!peek.StartsWith("pending\t", StringComparison.Ordinal)) continue;
            var parts = peek.Split('\t', 4);
            Console.WriteLine($"CluadeX strict approval — {parts[1]}  ({parts[2]})");
            Console.WriteLine("  " + (parts.Length > 3 ? parts[3].Replace("\\n", "\n  ") : ""));
            string done = Ask(pipe, JsonSerializer.Serialize(new { op = "approve", code }));
            Console.WriteLine(done == "approved" ? "Approved — this one command will run once." : "Not approved (" + done + ").");
            return done == "approved" ? 0 : 1;
        }
        Console.Error.WriteLine("CluadeX: no pending approval has that code (wrong, expired, already used, or CluadeX is not running).");
        return 1;
    }

    private static IEnumerable<string> AppPipes()
    {
        string[] all;
        try { all = Directory.GetFiles(@"\\.\pipe\"); }
        catch { yield break; }
        foreach (var p in all)
        {
            string name = Path.GetFileName(p);
            if (name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)) yield return name;
        }
    }

    private static string Ask(string pipe, string json)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            using var reader = new StreamReader(client, new UTF8Encoding(false));
            var read = reader.ReadLineAsync();
            return read.Wait(TimeSpan.FromSeconds(10)) ? read.Result ?? "" : "";
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    private static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(-1))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.WriteLine();
            }
        }
        catch { }
    }
}

/// <summary>
/// BrainX's ssh_run answer for a gated command: <c>needs_confirmation</c> with a confirm_id the owner approves by running
/// <c>brainx-mcp ssh-approve &lt;confirm_id&gt;</c> in THEIR terminal (BrainX refuses it from an agent's shell). CluadeX
/// shows it as a card; the approve step opens a terminal only on the owner's click, and CluadeX — not the model — resends
/// the identical call. The approve command is built here from the known BrainX path and a checked id: the one in the
/// tool output is never run (a tool result is data).
/// </summary>
public sealed record BrainxConfirmation(string ConfirmId, string Command, string Profile, IReadOnlyList<string> Reasons, string? ExpiresAt)
{
    /// <summary>Parses the tool's text (JSON somewhere in it). Null = not a needs_confirmation answer.</summary>
    public static BrainxConfirmation? TryParse(string? text, string? profileFromCall = null)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("needs_confirmation", StringComparison.Ordinal)) return null;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            bool needs = root.TryGetProperty("needs_confirmation", out var nc) && nc.ValueKind == JsonValueKind.True
                || Str(root, "status") == "needs_confirmation" || Str(root, "result") == "needs_confirmation";
            if (!needs) return null;
            string id = Str(root, "confirm_id") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{4,64}$")) return null;
            var reasons = new List<string>();
            if (root.TryGetProperty("reasons", out var rs))
            {
                if (rs.ValueKind == JsonValueKind.Array)
                    foreach (var r in rs.EnumerateArray()) { if (r.ValueKind == JsonValueKind.String) reasons.Add(r.GetString()!); }
                else if (rs.ValueKind == JsonValueKind.String) reasons.Add(rs.GetString()!);
            }
            return new BrainxConfirmation(id, Str(root, "command") ?? "",
                Str(root, "profile_id") ?? Str(root, "profile") ?? Str(root, "host") ?? profileFromCall ?? "", reasons, Str(root, "expires_at"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>brainx-mcp.exe where BrainX installs it, or null.</summary>
    public static string? BrainxExe()
    {
        string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brainx", "mcp", "brainx-mcp.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>The command the owner runs (copied, or opened in a terminal on their click).</summary>
    public string ApproveCommand => $"\"{BrainxExe() ?? "brainx-mcp"}\" ssh-approve {ConfirmId}";

    /// <summary>The text with the confirm id taken out — what the model may see.</summary>
    public string Redact(string? text) => (text ?? "").Replace(ConfirmId, "[hidden]", StringComparison.Ordinal);

    /// <summary>Opens a new console running BrainX's approve, for the owner to read and type yes. Only from the card's
    /// button. CluadeX's own variables are left out, so BrainX sees the owner's terminal, not an agent's shell.</summary>
    public bool OpenApproveTerminal()
    {
        if (BrainxExe() is not { } exe) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add("/k");
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add("ssh-approve");
            psi.ArgumentList.Add(ConfirmId);
            foreach (var key in psi.Environment.Keys.ToList())
                if (key.StartsWith("CLUADEX_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(key);
            System.Diagnostics.Process.Start(psi)?.Dispose();
            return true;
        }
        catch { return false; }
    }
}
