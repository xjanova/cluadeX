using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "BrainX" — the owner's cowork room as a model. A chat turn is posted into the BrainX cowork room as a request
/// from CluadeX (agent id <c>cluadex</c>), and whoever takes it answers there: a named agent ("@codex …") or, with no
/// name, whoever is free — the request also goes on the room's board, which the broker hands to a free runner (a
/// plain peer line calls nobody in by itself; BrainX.Mcp Program.Cowork.cs). Each responder's lines stream back into
/// the reply under their name, their code blocks rendered as they wrote them. Responders work on this machine and may
/// edit the open project's files directly (the owner's choice, 2026-10-16).
///
/// Like Claude Dev, the room is a set of complete agents: CluadeX wraps neither of its loops around it — one
/// delegated call per turn (CodeAgentService passthrough).
///
/// What it cannot do: post as the owner. Owner lines in the room are HMAC-sealed by the BrainX office window
/// (BusSeal, a DPAPI key); CluadeX's lines are peer lines — agents weigh them as requests, not orders.
/// </summary>
public sealed class BrainXRoomProvider : IAiProvider
{
    private readonly FileSystemService _fileSystem;
    /// <summary>The room has one "cluadex" seat: two chats relaying at once took each other's lines and the first to
    /// finish left the room for both (audit 2026-10-19). One relayed turn at a time; the next waits for the seat.</summary>
    private static readonly SemaphoreSlim Seat = new(1, 1);
    private static volatile bool _joined;
    /// <summary>The first line of the footer every relayed request carries (a loop marker as well).</summary>
    public const string RelayFooter = "Relayed from the owner's CluadeX chat";
    private string _status = "Not connected";

    /// <summary>What CluadeX's own rules let an agent do in the project for this turn (CodeAgentService sets it from the
    /// same WorkerGrant Claude Dev gets). Null = read-only: the request then says to propose changes, not make them —
    /// it used to say "you may edit files there directly" whatever the rules said.</summary>
    public static readonly AsyncLocal<ClaudeDevProvider.WorkerGrant?> Grant = new();

    /// <summary>No reply at all within this long = nobody took it (the owner's choice: 2 minutes).</summary>
    private static readonly TimeSpan NoReplyTimeout = TimeSpan.FromMinutes(2);
    /// <summary>…but a job left open on the board waits for the broker to hand it out: it gives an unclaimed card to a
    /// runner only after 15 minutes (BrainX Program.CoworkAllocate.cs), and the runner needs time to start. The 5 minutes
    /// this used to be gave up ten minutes before anyone could be called in.</summary>
    private static readonly TimeSpan NoReplyTimeoutBoard = TimeSpan.FromMinutes(17);
    /// <summary>After someone answered: the reply ends once they have been quiet this long without saying [done] —
    /// unless they still hold the board card ('doing' / 'blocked'): then the wait goes on to the hard cap.</summary>
    private static readonly TimeSpan QuietAfterReply = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HardCap = TimeSpan.FromMinutes(30);
    /// <summary>How often the board card is looked at while the room is quiet.</summary>
    private static readonly TimeSpan CardCheckEvery = TimeSpan.FromSeconds(60);

    /// <summary>Agents in the room whose lines are not answers: our own seat, the broker and the secretary.</summary>
    private static readonly HashSet<string> NotResponders = new(StringComparer.OrdinalIgnoreCase) { "cluadex", "broker", "mind" };

    public BrainXRoomProvider(FileSystemService fileSystem) => _fileSystem = fileSystem;

    public string ProviderId => "BrainX";
    public string DisplayName => "BrainX (cowork room)";
    public bool IsReady => BrainSyncService.Current?.IsBrainAvailable == true;
    public bool IsLoading => false;
    public string StatusMessage => IsReady ? "Connected to the BrainX cowork room" : _status;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _status = IsReady ? "Connected to the BrainX cowork room" : "BrainX is not connected (🧠 chip / MCP Servers)";
        OnStatusChanged?.Invoke(_status);
        return Task.CompletedTask;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var brain = BrainSyncService.Current;
        if (brain is not { IsBrainAvailable: true }) return (false, "BrainX is not connected");
        var (ok, text) = await brain.CallToolAsync("cowork_who", new Dictionary<string, object>(), ct);
        if (!ok) return (false, text);
        var online = RoomOnline(text);
        return (true, online.Count > 0 ? "In the room now: " + string.Join(", ", online) : "Connected — nobody is in the room right now");
    }

    public Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<List<string>?>(new List<string> { "cowork-room" });

    public async Task<string> GenerateAsync(List<ChatMessage> history, string userMessage, string? systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var s in ChatAsync(history, userMessage, systemPrompt, ct)) sb.Append(s);
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> ChatAsync(List<ChatMessage> history, string userMessage, string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<string>();
        var grant = Grant.Value;   // AsyncLocal: read here, the turn runs on the pool
        var work = Task.Run(async () =>
        {
            bool seated = false;
            try
            {
                if (!await Seat.WaitAsync(0, ct))
                {
                    channel.Writer.TryWrite("⏳ อีกแชทกำลังใช้ห้อง cowork อยู่ — รอให้รอบนั้นจบก่อน แล้วจะส่งให้ต่อ…\n\n");
                    await Seat.WaitAsync(ct);
                }
                seated = true;
                await RunTurnAsync(userMessage, grant, s => channel.Writer.TryWrite(s), ct);
            }
            catch (OperationCanceledException) { channel.Writer.TryWrite("\n\n⏹ หยุดรอแล้ว"); }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                channel.Writer.TryWrite($"\n\n⚠ BrainX: {ex.Message}");
            }
            finally
            {
                channel.Writer.TryComplete();
                // 5. Out of the room between turns: a seat left behind would put room traffic ("addressedToYou") into
                //    every brain tool reply of CluadeX's ordinary runs — the agent would start answering the room.
                if (seated && _joined && BrainSyncService.Current is { } b)
                {
                    try
                    {
                        using var leaveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await b.CallToolAsync("cowork_leave", new Dictionary<string, object>(), leaveCts.Token);
                    }
                    catch { }
                    _joined = false;
                }
                if (seated) Seat.Release();
            }
        });
        await foreach (var s in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return s;
        await work;
    }

    // ─── one turn ───

    /// <summary>"@codex แก้ X" → ("codex", "แก้ X"); "@claude,codex …" → both. No @ = whoever is free (null).</summary>
    public static (string? To, string Text) ParseTarget(string message)
    {
        var m = Regex.Match(message.TrimStart(), @"^@([A-Za-z][\w-]*(?:\s*,\s*[A-Za-z][\w-]*)*)\s+([\s\S]+)$");
        if (!m.Success) return (null, message.Trim());
        string to = Regex.Replace(m.Groups[1].Value, @"\s+", "").ToLowerInvariant();
        return (to, m.Groups[2].Value.Trim());
    }

    private async Task RunTurnAsync(string userMessage, ClaudeDevProvider.WorkerGrant? grant, Action<string> emit, CancellationToken ct)
    {
        // ─── Loop guards ───
        // 1. A run another program started over the pipe (the room's broker handing work to cluadex, a script) is
        //    never relayed back into the room — room → CluadeX → room → … would never end.
        if (AgentRunScope.Current is { FromPipe: true })
        {
            emit("⚠ งานนี้ถูกส่งมาจากภายนอก (ผ่าน pipe ของ CluadeX เช่นห้อง cowork) — โมเดล BrainX จะไม่ส่งต่อกลับเข้าห้องอีก เพราะจะวนลูป "
                 + "เลือกโมเดลอื่น (เช่น Claude API หรือโมเดลในเครื่อง) ให้ CluadeX ทำงานนี้เอง");
            return;
        }
        // 2. A request that already went through a room relay (our own footer is in it) is not relayed again.
        if (userMessage.Contains(RelayFooter, StringComparison.Ordinal))
        {
            emit("⚠ ข้อความนี้เคยถูกส่งผ่านห้อง cowork มาแล้ว — ไม่ส่งซ้ำเพื่อกันการวนลูป");
            return;
        }
        var (to, text) = ParseTarget(userMessage);
        // 3. Never address ourselves: "@cluadex" would hand the job to this very seat.
        if (to != null)
        {
            to = string.Join(",", to.Split(',').Where(a => !NotResponders.Contains(a)));
            if (to.Length == 0) to = null;
        }
        if (text.Length == 0) { emit("พิมพ์คำถามหรือคำสั่งที่จะส่งเข้าห้องด้วยครับ"); return; }
        var brain = BrainSyncService.Current;
        if (brain is not { IsBrainAvailable: true })
        {
            emit("⚠ BrainX ยังไม่เชื่อมต่อ — เปิด BrainX แล้วดูที่ชิป 🧠 หรือหน้า MCP Servers ก่อน");
            return;
        }

        string topic = "cx-" + Guid.NewGuid().ToString("N")[..6];
        string project = _fileSystem.HasWorkingDirectory ? _fileSystem.WorkingDirectory : "";
        var folders = new List<string>();
        if (project.Length > 0) folders.Add(project);
        if (AgentRunScope.Current is { } scope) folders.AddRange(scope.ExtraFolders);
        string work = "cluadex-" + (project.Length > 0 ? Slug(Path.GetFileName(project.TrimEnd('\\', '/'))) : "chat");

        // A seat first: the cursor starts now, and without joining cowork_read never advances (Program.Cowork.cs).
        var (joinOk, joinText) = await brain.CallToolAsync("cowork_join", new Dictionary<string, object>
        {
            ["catch_up"] = 0,
            ["work"] = work,
            ["display"] = "CluadeX (owner's chat)",
            ["skills"] = new[] { "relays the owner's chat requests" },
            // 4. The room must not route work to this seat — it only relays.
            ["cannot"] = new[] { "take tasks in the room — this seat only relays the owner's CluadeX chat; never assign or address work to cluadex" },
        }, ct);
        _joined = joinOk;
        if (!joinOk || joinText.Contains("\"roomOpen\":false") || joinText.Contains("\"joined\":false"))
        {
            emit("⚠ เข้าห้อง cowork ไม่ได้ — ห้องอาจปิดอยู่ เปิดห้องจากหน้าต่าง BrainX (Cowork) ก่อน แล้วส่งใหม่"
                 + (ReasonOf(joinText) is { } why ? "\n\n" + why : ""));
            return;
        }

        string request = BuildRequest(text, topic, folders, grant);
        var sayArgs = new Dictionary<string, object> { ["message"] = request, ["topic"] = topic, ["work"] = work };
        if (to != null) sayArgs["to"] = to;
        var (sayOk, sayText) = await brain.CallToolAsync("cowork_say", sayArgs, ct);
        if (!sayOk)
        {
            emit("⚠ ส่งเข้าห้องไม่สำเร็จ" + (ReasonOf(sayText) is { } why ? ": " + why : ""));
            return;
        }
        var online = RoomOnline(sayText).Where(a => !NotResponders.Contains(a)).ToList();

        // Whoever is free: a peer line calls nobody in by itself — the board does (the broker hands an open card to a
        // free runner). A named agent that is not in the room gets the card too, assigned to them.
        bool onBoard = false;
        string? cardId = null;
        if (to == null || !to.Split(',').All(a => online.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            var taskArgs = new Dictionary<string, object>
            {
                ["action"] = "add",
                ["title"] = Trim(CardTitle(text), 80),
                ["detail"] = Trim(request, 3900),
                ["work"] = work,
            };
            if (to != null && !to.Contains(',')) taskArgs["assignee"] = to;
            var (taskOk, taskText) = await brain.CallToolAsync("cowork_task", taskArgs, ct);
            cardId = taskOk ? CardIdOf(taskText) : null;
            onBoard = taskOk;
        }

        emit("📨 ส่งเข้าห้อง cowork แล้ว · ถึง: " + (to ?? "ใครก็ได้ที่ว่าง")
             + (online.Count > 0 ? " · ในห้องตอนนี้: " + string.Join(", ", online) : " · ตอนนี้ไม่มีใครนั่งในห้อง")
             + (onBoard ? " · ลงบอร์ดงานแล้ว" + (cardId != null ? $" [{cardId}]" : "") : "") + "\n\n");
        TimeSpan noReply = online.Count == 0 || onBoard && to == null ? NoReplyTimeoutBoard : NoReplyTimeout;
        if (noReply == NoReplyTimeoutBoard && to == null)
            emit($"_(ถ้ายังไม่มีใครหยิบงาน broker ของห้องจะส่งให้เอเจนต์ที่ว่างภายใน ~15 นาที — รอได้ถึง {(int)noReply.TotalMinutes} นาที หรือระบุชื่อ เช่น @codex ให้เริ่มเร็วขึ้น)_\n\n");

        bool finished = false;
        try
        {
            finished = await WaitForAnswersAsync(brain, topic, to, cardId, online, noReply, emit, ct);
        }
        finally
        {
            // Stopped, or given up with nobody on it: take the card off the board, so nobody does the work later with
            // nobody watching (the audit's "Stop leaves the card open"). A card an agent finished is theirs to close.
            if (!finished && cardId != null)
            {
                try
                {
                    using var dropCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await brain.CallToolAsync("cowork_task", new Dictionary<string, object>
                    {
                        ["action"] = "update",
                        ["id"] = cardId,
                        ["status"] = "dropped",
                        ["note"] = ct.IsCancellationRequested ? "the owner stopped this in CluadeX" : "nobody took it in time — the owner's CluadeX chat stopped waiting",
                    }, dropCts.Token);
                }
                catch { }
            }
        }
    }

    /// <summary>Read the room until the answer is complete. True = finished (every wanted responder said [done] or closed
    /// the card); false = gave up (nobody took it, quiet too long, the hard cap) — the caller then drops the card.</summary>
    private async Task<bool> WaitForAnswersAsync(BrainSyncService brain, string topic, string? to, string? cardId,
        List<string> online, TimeSpan noReply, Action<string> emit, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        DateTime? lastReply = null;
        DateTime lastCardCheck = DateTime.UtcNow;
        bool saidStillWorking = false;
        string? lastSpeaker = null;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = to?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Whoever the board says holds our card: their lines are answers even when they forget to:'cluadex'.
        var holders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (readOk, readText) = await brain.CallToolAsync("cowork_read", new Dictionary<string, object>
            {
                ["wait_seconds"] = 10,
                ["limit"] = 50,
            }, ct);
            if (readOk)
            {
                foreach (var line in ParseLines(readText))
                {
                    if (line.Mine || NotResponders.Contains(line.From) || line.From.Contains("owner", StringComparison.OrdinalIgnoreCase))
                    {
                        // The broker saying nobody can be called is worth showing.
                        if (line.From.Equals("broker", StringComparison.OrdinalIgnoreCase) && line.Addressed is "you")
                            emit($"\n\n> 🛎 {line.Body.Trim()}\n");
                        continue;
                    }
                    if (!IsForThisTurn(line, topic, cardId, wanted) && !holders.Contains(line.From)) continue;
                    string body = line.Body;
                    bool isDone = Regex.IsMatch(body, @"\[done\]", RegexOptions.IgnoreCase) || ClosesCard(body, cardId);
                    body = Regex.Replace(body, @"\s*\[done\]\s*", "\n", RegexOptions.IgnoreCase).TrimEnd();
                    if (!string.Equals(lastSpeaker, line.From, StringComparison.OrdinalIgnoreCase))
                        emit($"\n\n### 💬 {line.From}\n\n");
                    else emit("\n\n");
                    emit(body);
                    lastSpeaker = line.From;
                    lastReply = DateTime.UtcNow;
                    answered.Add(line.From);
                    if (isDone) done.Add(line.From);
                }
            }

            var now = DateTime.UtcNow;
            bool allDone = answered.Count > 0 && (wanted == null ? done.Count > 0 : wanted.All(done.Contains));
            if (allDone) { emit("\n\n✅ เสร็จแล้ว (" + string.Join(", ", done) + ")"); return true; }

            // The board says more than the chat: a card someone holds is work in progress however quiet the room is,
            // and a card closed without a [done] line is finished.
            string? card = null;
            if (cardId != null && now - lastCardCheck > CardCheckEvery)
            {
                lastCardCheck = now;
                var (listOk, listText) = await brain.CallToolAsync("cowork_task", new Dictionary<string, object> { ["action"] = "list" }, ct);
                (card, string? holder) = listOk ? CardStatus(listText, cardId) : ("unknown", null);
                if (holder != null && !NotResponders.Contains(holder)) holders.Add(holder);
                if (card == "gone" && answered.Count > 0) { emit("\n\n✅ งานบนบอร์ดปิดแล้ว (" + string.Join(", ", answered) + ")"); return true; }
            }
            bool held = card is "doing" or "blocked";

            if (lastReply == null && now - started > noReply && !held)
            {
                emit($"⏱ ไม่มีใครรับภายใน {(int)noReply.TotalMinutes} นาที"
                     + (cardId != null ? " — เอางานออกจากบอร์ดแล้ว จะได้ไม่มีใครทำต่อโดยไม่มีคนดู" : "")
                     + "\nลองระบุชื่อ เช่น @codex หรือ @claude หรือเปิดเอเจนต์เข้าห้อง cowork ก่อน");
                return false;
            }
            if (lastReply != null && now - lastReply > QuietAfterReply)
            {
                if (held)
                {
                    if (!saidStillWorking)
                        emit($"\n\n_(ห้องเงียบไป {(int)QuietAfterReply.TotalMinutes} นาที แต่งาน [{cardId}] ยังมีคนถืออยู่ — รอต่อ)_");
                    saidStillWorking = true;
                }
                else if (cardId == null || card != null)   // without a card, or the card was just looked at and nobody holds it
                {
                    emit($"\n\n_(เงียบไป {(int)QuietAfterReply.TotalMinutes} นาทีหลังคำตอบล่าสุด และไม่มีใครถืองานนี้อยู่ — จบรอบนี้)_");
                    return false;
                }
            }
            if (now - started > HardCap) { emit("\n\n_(ครบเวลารอสูงสุด 30 นาที)_"); return false; }
        }
    }

    /// <summary>A room line belongs to this turn: addressed to us, on our topic, about our card, or — with named agents —
    /// from one of them. With nobody named, a line from anyone counts only when it is addressed to us, on the topic or
    /// about the card: every other line in the room used to pass (unrelated chatter, other cards).</summary>
    public static bool IsForThisTurn(RoomLine line, string topic, string? cardId, HashSet<string>? wanted) =>
        line.Addressed == "you" || line.Topic == topic
        || (cardId != null && line.Body.Contains("[" + cardId + "]", StringComparison.Ordinal))
        || (wanted != null && wanted.Contains(line.From));

    /// <summary>"✅ codex เสร็จแล้ว [t-3f9a1c] …" — the board's own line when an agent closes our card.</summary>
    public static bool ClosesCard(string body, string? cardId) =>
        cardId != null && body.Contains("[" + cardId + "]", StringComparison.Ordinal) && body.TrimStart().StartsWith("✅", StringComparison.Ordinal);

    /// <summary>The card id from cowork_task add's reply ({"ok":true,"task":{"id":"t-…"}}), or null.</summary>
    public static string? CardIdOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("task", out var t)
                && t.ValueKind == JsonValueKind.Object && Str(t, "id") is { Length: > 0 } id ? id : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Our card on the board (cowork_task list: unfinished cards only): its status and who holds it, or "gone"
    /// when it is no longer there (done or dropped), "unknown" when the reply cannot be read.</summary>
    public static (string Status, string? Assignee) CardStatus(string listJson, string cardId)
    {
        try
        {
            using var doc = JsonDocument.Parse(listJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("board", out var board)) return ("unknown", null);
            foreach (var card in Cards(board))
                if (Str(card, "id") == cardId) return (Str(card, "status") ?? "open", Str(card, "assignee"));
            return ("gone", null);
        }
        catch (JsonException) { return ("unknown", null); }

        static IEnumerable<JsonElement> Cards(JsonElement e)
        {
            // The board is an array of cards, or an object of arrays by lane — accept both.
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in e.EnumerateArray()) if (c.ValueKind == JsonValueKind.Object) yield return c;
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var c in p.Value.EnumerateArray()) if (c.ValueKind == JsonValueKind.Object) yield return c;
            }
        }
    }

    /// <summary>A failed join/say says why in words ("note"/"error"/"message" of the reply), never as raw JSON.</summary>
    public static string? ReasonOf(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "note", "error", "message", "reason" })
                    if (Str(doc.RootElement, key) is { Length: > 0 } s) return Trim(s, 300);
            return null;
        }
        catch (JsonException) { return Trim(reply.Trim(), 300); }
    }

    public static string BuildRequest(string text, string topic, List<string> folders, ClaudeDevProvider.WorkerGrant? grant = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(text);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(RelayFooter + " (the owner is waiting there for your answer). Do not hand this back to cluadex.");
        if (folders.Count > 0)
        {
            // What the owner's own CluadeX rules allow in this project — the same rule Claude Dev runs under.
            string may = grant is { Edit: true } ? "you may edit files there directly"
                : "the owner's rules do NOT allow edits there from this chat — read only, and propose changes as diffs";
            if (grant is { Run: true }) may += "; you may run builds/tests there";
            sb.AppendLine("Project folder(s) on this PC — " + may + ": " + string.Join(", ", folders));
        }
        sb.AppendLine($"Reply in this room with to:'cluadex' and topic:'{topic}'. Put code in fenced blocks with the file path "
            + "on the line above each block. When you are finished, end your last line with [done].");
        return sb.ToString();
    }

    // ─── reading the room's JSON ───

    public sealed record RoomLine(string From, string Body, string Addressed, string? Topic, bool Mine);

    /// <summary>The lines of a cowork_read result (tolerant: any array of objects with from/body under "lines"/"messages").</summary>
    public static List<RoomLine> ParseLines(string json)
    {
        var list = new List<RoomLine>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement arr = default;
            bool found = false;
            foreach (var name in new[] { "lines", "messages", "items" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array)
                { found = true; break; }
            if (!found && doc.RootElement.ValueKind == JsonValueKind.Array) { arr = doc.RootElement; found = true; }
            if (!found) return list;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string from = Str(e, "from") ?? "";
                string body = Str(e, "body") ?? Str(e, "message") ?? "";
                if (from.Length == 0 || body.Length == 0) continue;
                bool mine = e.TryGetProperty("mine", out var m) && m.ValueKind == JsonValueKind.True;
                list.Add(new RoomLine(from, body, Str(e, "addressed") ?? "", Str(e, "topic"), mine));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Agents online in the room, from any result carrying room[].</summary>
    public static List<string> RoomOnline(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("room", out var room)
                && room.ValueKind == JsonValueKind.Array)
                foreach (var r in room.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.True
                        && Str(r, "agent") is { Length: > 0 } a)
                        list.Add(a);
        }
        catch (JsonException) { }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();

    /// <summary>The board card's title: the request's first line — and for a handoff ("ทำต่อ" + AgentHandoff's brief)
    /// the task's first line after it, so the card says which task, not just "ทำต่อ".</summary>
    public static string CardTitle(string text)
    {
        string first = FirstLine(text);
        int at = text.IndexOf("### The task", StringComparison.Ordinal);
        if (at < 0) return first;
        var lines = text[at..].Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0);
        string task = lines.FirstOrDefault() ?? "";
        return task.Length == 0 || task.StartsWith("###", StringComparison.Ordinal) ? first : first + " · " + task;
    }
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } x ? x : "project";

    public void Dispose() { }
}
