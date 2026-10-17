using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;

namespace ChatTest;

/// <summary>
/// cluadex.list_chats / cluadex.continue_chat through App.HandleMcpToolAsync (the pipe's real dispatcher): an
/// orchestrator finds a chat and carries it on without clicking the sidebar.
/// </summary>
internal static partial class Program
{
    static string ListProj => Path.Combine(PipeRoot, "list-project");

    static List<JsonElement> ChatsOf(McpToolResult r)
    {
        using var doc = JsonDocument.Parse(r.GetTextOutput());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    static string Js(string s) => JsonSerializer.Serialize(s);

    static async Task TestPipeChats()
    {
        Log("\n## T6 — cluadex.list_chats / continue_chat: find a chat and carry it on over the pipe");
        Directory.CreateDirectory(ProjA);
        Directory.CreateDirectory(ProjB);
        if (Directory.Exists(ListProj)) Directory.Delete(ListProj, true);
        Directory.CreateDirectory(ListProj);

        // Three stored chats of one project, saved oldest first (the database stamps updated_at at save time).
        ChatSession Stored(string title, string lastReply)
        {
            var s = new ChatSession { Title = title, ProjectPath = ListProj };
            s.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "task " + title });
            s.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = lastReply });
            Persist.SaveSession(s);
            Thread.Sleep(30);
            return s;
        }
        var oldest = Stored("alpha chat", "All done.");
        var paused = Stored("hive-t2", AgentRunScope.PausedMarker + " Paused after step 101 — the work so far is kept.");
        var newest = Stored("beta chat", "Finished.");

        // L — the list: filtered by project, newest first, paused read from the stored reply, nothing on screen moves.
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();
        var screenBefore = Vm.CurrentSession;
        var rowsBefore = Vm.Messages;
        var list = await Dispatch("cluadex.list_chats", "{\"project\":" + Js(ListProj + "\\") + "}");
        var rows = list.IsError ? new() : ChatsOf(list);
        var ids = rows.Select(r => r.GetProperty("id").GetString()).ToList();
        Check("L1 list_chats with a project gives only that folder's chats, newest first (a trailing slash matches)",
            !list.IsError && ids.SequenceEqual([newest.Id, paused.Id, oldest.Id]), list.GetTextOutput());
        bool PausedOf(string id) => rows.First(r => r.GetProperty("id").GetString() == id).GetProperty("paused").GetBoolean();
        Check("L2 paused = the chat's last reply is a Pause (read from the stored chat); running = false",
            rows.Count == 3 && PausedOf(paused.Id) && !PausedOf(oldest.Id) && !PausedOf(newest.Id)
            && rows.All(r => !r.GetProperty("running").GetBoolean()), list.GetTextOutput());
        Check("L3 every row has title, project and an ISO 8601 updatedAt",
            rows.Count == 3 && rows.All(r => r.GetProperty("title").GetString()!.Length > 0
                && SamePath(r.GetProperty("project").GetString()!, ListProj)
                && DateTimeOffset.TryParseExact(r.GetProperty("updatedAt").GetString(), "o", null, System.Globalization.DateTimeStyles.None, out _)),
            list.GetTextOutput());
        var q = await Dispatch("cluadex.list_chats", "{\"query\":\"HIVE-T\"}");
        var qIds = q.IsError ? new() : ChatsOf(q).Select(r => r.GetProperty("id").GetString()).ToList();
        Check("L4 query matches part of the title, case-insensitive", qIds.SequenceEqual([paused.Id]), q.GetTextOutput());
        var one = await Dispatch("cluadex.list_chats", "{\"project\":" + Js(ListProj) + ",\"limit\":1}");
        Check("L5 limit=1 gives the newest chat only", !one.IsError && ChatsOf(one).Select(r => r.GetProperty("id").GetString()).SequenceEqual([newest.Id]), one.GetTextOutput());
        Check("L6 list_chats is read-only: the chat on screen and its rows are untouched",
            ReferenceEquals(Vm.CurrentSession, screenBefore) && ReferenceEquals(Vm.Messages, rowsBefore));

        // V — argument checks.
        var badLimits = new[] { "{\"limit\":0}", "{\"limit\":201}", "{\"limit\":\"lots\"}" };
        var limitResults = new List<McpToolResult>();
        foreach (var a in badLimits) limitResults.Add(await Dispatch("cluadex.list_chats", a));
        Check("V1 a limit outside 1-200 (or not a number) is refused", limitResults.All(r => r.IsError && r.GetTextOutput().Contains("limit")),
            string.Join(" / ", limitResults.Select(r => r.GetTextOutput())));
        var noMsg = await Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(paused.Id) + "}");
        var noChat = await Dispatch("cluadex.continue_chat", "{\"message\":\"continue\"}");
        var badSteps = await Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(paused.Id) + ",\"message\":\"continue\",\"maxSteps\":-1}");
        var provOnly = await Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(paused.Id) + ",\"message\":\"continue\",\"provider\":\"Anthropic\"}");
        var badProv = await Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(paused.Id) + ",\"message\":\"continue\",\"provider\":\"Nope\",\"model\":\"x\"}");
        Check("V2 continue_chat refuses a missing message, a missing chatId/title, a bad maxSteps, a provider without a model, an unknown provider",
            noMsg.IsError && noMsg.GetTextOutput().Contains("message") && noChat.IsError && noChat.GetTextOutput().Contains("chatId")
            && badSteps.IsError && badSteps.GetTextOutput().Contains("maxSteps") && provOnly.IsError && provOnly.GetTextOutput().Contains("model")
            && badProv.IsError && badProv.GetTextOutput().Contains("Unknown provider"),
            string.Join(" / ", new[] { noMsg, noChat, badSteps, provOnly, badProv }.Select(r => r.GetTextOutput())));
        Check("V2 …and none of them opened a chat or sent anything", ReferenceEquals(Vm.CurrentSession, screenBefore) && Vm.Messages.Count == rowsBefore.Count);

        // C1 — an id that does not exist.
        var unknown = await Dispatch("cluadex.continue_chat", "{\"chatId\":\"no-such-chat-id\",\"message\":\"continue\"}");
        var unknownTitle = await Dispatch("cluadex.continue_chat", "{\"title\":\"no such title\",\"message\":\"continue\"}");
        Check("C1 continue_chat on an unknown id or title is an error, and nothing on screen changes",
            unknown.IsError && unknown.GetTextOutput().Contains("No chat with id") && unknownTitle.IsError && unknownTitle.GetTextOutput().Contains("No chat titled")
            && ReferenceEquals(Vm.CurrentSession, screenBefore), unknown.GetTextOutput() + " / " + unknownTitle.GetTextOutput());

        // C2 — a chat that is running: listed as running, and continue_chat is refused.
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Hang());
        Vm.SetWorkingDirectory(ProjB);
        await FreshChat();
        await Send("Work on something long.");
        bool running = await WaitUntil(() => Vm.IsGenerating, 10000);
        var runningChat = Vm.CurrentSession!;
        var listRunning = await Dispatch("cluadex.list_chats", "{\"project\":" + Js(ProjB) + "}");
        bool listedRunning = !listRunning.IsError && ChatsOf(listRunning).Any(r => r.GetProperty("id").GetString() == runningChat.Id && r.GetProperty("running").GetBoolean());
        int callsBefore = Fake.Calls.Count;
        var busy = await Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(runningChat.Id) + ",\"message\":\"and this too\"}");
        Check("C2 a running chat is listed with running=true", running && listedRunning, listRunning.GetTextOutput());
        Check("C2 continue_chat on a running chat is an error and sends nothing into it",
            busy.IsError && busy.GetTextOutput().Contains("already running") && Fake.Calls.Count == callsBefore
            && !Vm.Messages.Any(m => m.Content == "and this too"), busy.GetTextOutput());
        Vm.StopGenerationCommand.Execute(null);
        await WaitIdle();

        // C3 — carry on the paused chat by its title: opened with its project, sent as the user, the reply comes back.
        Vm.SetWorkingDirectory(ProjA);
        await FreshChat();
        Vm.UserInput = "my own draft";
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Reply("Carried on from step 101", " — all steps are done."));
        callsBefore = Fake.Calls.Count;
        var go = await Dispatch("cluadex.continue_chat", "{\"title\":\"HIVE-T2\",\"message\":\"continue\"}");
        await Render();
        Check("C3 continue_chat returns the turn's final reply with isError=false",
            !go.IsError && go.GetTextOutput().Contains("all steps are done"), go.GetTextOutput());
        Check("C3 the chat is opened in the window, with its own project (as a click in the sidebar)",
            Vm.CurrentSession?.Id == paused.Id && SamePath(Vm.WorkingDirectory, ListProj) && WindowMatchesChat(), $"{Vm.CurrentSession?.Title} / {Vm.WorkingDirectory}");
        Check("C3 the message is sent as the user and the model is given the chat's history",
            Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == "continue")
            && Fake.Calls.Skip(callsBefore).Any(c => c.Input.Contains("continue") && c.History.Any(h => h.Content.Contains("Paused after step 101"))),
            string.Join(" | ", Vm.Messages.Select(m => $"{m.Role}:{m.Content}")));
        Check("C3 the user's draft in the input box is left alone", Vm.UserInput == "my own draft", Vm.UserInput);
        var stored = Persist.LoadSession(paused.Id);
        Check("C3 the turn is saved in that chat", stored != null && stored.Messages.Any(m => m.Content == "continue")
            && stored.Messages.Any(m => m.Content.Contains("all steps are done")));
        Vm.UserInput = "";

        // C4 — the caller cancelling stops the turn: isError, and the chat is idle again.
        Fake.Script.Clear();
        Fake.Script.Enqueue(FakeStep.Hang());
        using (var cts = new CancellationTokenSource())
        {
            var call = Dispatch("cluadex.continue_chat", "{\"chatId\":" + Js(oldest.Id) + ",\"message\":\"keep going\"}", cts.Token);
            bool began = await WaitUntil(() => Vm.IsGenerating && Vm.CurrentSession?.Id == oldest.Id, 10000);
            cts.Cancel();
            var r = await Task.WhenAny(call, Task.Delay(20000)) == call ? await call : null;
            await Render();
            Check("C4 the caller cancelling stops the turn: isError 'Cancelled', the chat is idle",
                began && r is { IsError: true } && r.GetTextOutput().Contains("Cancelled") && !Vm.IsGenerating, r?.GetTextOutput() ?? "no result");
        }
        Fake.Script.Clear();
    }
}
