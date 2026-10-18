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
    private volatile bool _joined;
    /// <summary>The first line of the footer every relayed request carries (a loop marker as well).</summary>
    public const string RelayFooter = "Relayed from the owner's CluadeX chat";
    private string _status = "Not connected";

    /// <summary>No reply at all within this long = nobody took it (the owner's choice: 2 minutes).</summary>
    private static readonly TimeSpan NoReplyTimeout = TimeSpan.FromMinutes(2);
    /// <summary>…but a job left on the board for the broker to hand out waits longer: it calls a runner in after a
    /// grace of its own (FollowUp.cs, 2 minutes), and the runner needs time to start.</summary>
    private static readonly TimeSpan NoReplyTimeoutBoard = TimeSpan.FromMinutes(5);
    /// <summary>After someone answered: the reply ends once they have been quiet this long without saying [done].</summary>
    private static readonly TimeSpan QuietAfterReply = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HardCap = TimeSpan.FromMinutes(30);

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
        var work = Task.Run(async () =>
        {
            try { await RunTurnAsync(userMessage, s => channel.Writer.TryWrite(s), ct); }
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
                if (_joined && BrainSyncService.Current is { } b)
                {
                    try
                    {
                        using var leaveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await b.CallToolAsync("cowork_leave", new Dictionary<string, object>(), leaveCts.Token);
                    }
                    catch { }
                    _joined = false;
                }
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

    private async Task RunTurnAsync(string userMessage, Action<string> emit, CancellationToken ct)
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
            emit("⚠ เข้าห้อง cowork ไม่ได้ — ห้องอาจปิดอยู่ เปิดห้องจากหน้าต่าง BrainX (Cowork) ก่อน แล้วส่งใหม่\n\n" + Trim(joinText, 300));
            return;
        }

        string request = BuildRequest(text, topic, folders);
        var sayArgs = new Dictionary<string, object> { ["message"] = request, ["topic"] = topic, ["work"] = work };
        if (to != null) sayArgs["to"] = to;
        var (sayOk, sayText) = await brain.CallToolAsync("cowork_say", sayArgs, ct);
        if (!sayOk)
        {
            emit("⚠ ส่งเข้าห้องไม่สำเร็จ: " + Trim(sayText, 400));
            return;
        }
        var online = RoomOnline(sayText).Where(a => !NotResponders.Contains(a)).ToList();

        // Whoever is free: a peer line calls nobody in by itself — the board does (the broker hands an open card to a
        // free runner). A named agent that is not in the room gets the card too, assigned to them.
        bool onBoard = false;
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
            var (taskOk, _) = await brain.CallToolAsync("cowork_task", taskArgs, ct);
            onBoard = taskOk;
        }

        emit("📨 ส่งเข้าห้อง cowork แล้ว · ถึง: " + (to ?? "ใครก็ได้ที่ว่าง")
             + (online.Count > 0 ? " · ในห้องตอนนี้: " + string.Join(", ", online) : " · ตอนนี้ไม่มีใครนั่งในห้อง")
             + (onBoard ? " · ลงบอร์ดงานแล้ว" : "") + "\n\n");

        var started = DateTime.UtcNow;
        DateTime? lastReply = null;
        string? lastSpeaker = null;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = to?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TimeSpan noReply = online.Count == 0 || onBoard && to == null ? NoReplyTimeoutBoard : NoReplyTimeout;

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
                    bool forUs = line.Addressed == "you" || line.Topic == topic
                                 || wanted == null || wanted.Contains(line.From);
                    if (!forUs) continue;
                    string body = line.Body;
                    bool isDone = Regex.IsMatch(body, @"\[done\]", RegexOptions.IgnoreCase);
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
            if (allDone) { emit("\n\n✅ เสร็จแล้ว (" + string.Join(", ", done) + ")"); return; }
            if (lastReply == null && now - started > noReply)
            {
                emit($"⏱ ไม่มีใครรับภายใน {(int)noReply.TotalMinutes} นาที"
                     + (onBoard ? " — งานยังอยู่บนบอร์ดของห้อง ใครว่างจะหยิบไปทำต่อได้" : "")
                     + "\nลองระบุชื่อ เช่น @codex หรือ @claude หรือเปิดเอเจนต์เข้าห้อง cowork ก่อน");
                return;
            }
            if (lastReply != null && now - lastReply > QuietAfterReply)
            {
                emit($"\n\n_(เงียบไป {(int)QuietAfterReply.TotalMinutes} นาทีหลังคำตอบล่าสุด — จบรอบนี้ ถ้ายังทำต่ออยู่ คำตอบจะอยู่ในห้อง cowork)_");
                return;
            }
            if (now - started > HardCap) { emit("\n\n_(ครบเวลารอสูงสุด 30 นาที)_"); return; }
        }
    }

    private static string BuildRequest(string text, string topic, List<string> folders)
    {
        var sb = new StringBuilder();
        sb.AppendLine(text);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(RelayFooter + " (the owner is waiting there for your answer). Do not hand this back to cluadex.");
        if (folders.Count > 0)
            sb.AppendLine("Project folder(s) on this PC — you may edit files there directly: " + string.Join(", ", folders));
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
