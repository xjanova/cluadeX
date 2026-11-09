// Read aloud only the reply bubble (owner 2026-11-09: "ให้น้องพูดโต้ตอบเสียงเฉพาะบล็อกแบบนี้"): SpokenReply.Of picks the
// bubble that ends a turn and passes over cards, notices and status rows; a turn that ends on a tool row or the app's
// "no summary" line reads nothing — never an earlier bubble in its place.
using CluadeX.Models;
using CluadeX.Services.Voice;

namespace ApiTest;

internal static class SpokenReplyWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Voice — only the reply bubble is read aloud");

        static ChatMessage U(string t) => new() { Role = MessageRole.User, Content = t };
        static ChatMessage A(string t) => new() { Role = MessageRole.Assistant, Content = t };
        static ChatMessage Tool() => new() { Role = MessageRole.ToolAction, Content = "read_file a.cs", ToolName = "read_file" };
        static ChatMessage Card(string tag) => new() { Role = MessageRole.System, Content = "{}", ToolName = tag };

        var final = A("ขึ้น GitHub แล้วค่ะ commit `e5f315e`");
        var rows = new List<ChatMessage>
        {
            U("old"), A("old reply"),
            U("push"), A("ขอดูก่อนนะคะ"), Tool(), Tool(), final,
            Card(ChatMessage.TurnChangesTag), Card(ChatMessage.NextStepTag),
            new() { Role = MessageRole.AgentStatus, Content = "⏳" },
        };
        check("voice", "the bubble that ends the turn is read, past the changes card and the next-step box",
            SpokenReply.Of(rows, 2) == final, "");
        var typedDuring = new List<ChatMessage> { U("go"), Tool(), A("เสร็จแล้วค่ะ"), U("ขอบคุณ") };
        check("voice", "a note the user typed while she worked does not hide her reply", SpokenReply.Of(typedDuring, 0)?.Content == "เสร็จแล้วค่ะ", "");

        var noSummary = new List<ChatMessage> { U("go"), A("ขอดูไฟล์ก่อนนะคะ"), Tool(), new() { Role = MessageRole.Assistant, Content = "*ran 2 tools*", IsTurnNote = true } };
        check("voice", "the app's 'no summary' line is not read, nor the bubble before the tools", SpokenReply.Of(noSummary, 0) == null, "");

        var endsOnTool = new List<ChatMessage> { U("go"), A("ขอดูไฟล์ก่อนนะคะ"), Tool() };
        check("voice", "a turn that ended on a tool row reads nothing", SpokenReply.Of(endsOnTool, 0) == null, "");

        var folded = new List<ChatMessage> { U("go"), A("ตอบแล้วค่ะ"), new() { Role = MessageRole.Assistant, Content = "opening Grok", FoldedIntoBalloon = true } };
        check("voice", "a line folded into a tool balloon is passed over", SpokenReply.Of(folded, 0)?.Content == "ตอบแล้วค่ะ", "");

        var bad = new[]
        {
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", HasError = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", IsInterrupted = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", IsStreaming = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "   " },
        };
        check("voice", "an error, a stopped reply, one still streaming or an empty one is not read",
            bad.All(b => SpokenReply.Of(new List<ChatMessage> { U("go"), b }, 0) == null), "");
        check("voice", "a missing user row reads nothing", SpokenReply.Of(rows, -1) == null && SpokenReply.Of(rows, 99) == null, "");

        // The Voice log line says why a turn was not read.
        string why = SpokenReply.Describe(SpokenReply.LastRow(endsOnTool, 0));
        check("voice", "the log names the row a silent turn ended on", why == "ToolAction read_file", why);
        string streaming = SpokenReply.Describe(SpokenReply.LastRow(new List<ChatMessage> { U("go"), bad[2] }, 0));
        check("voice", "the log says the reply was still streaming", streaming == "Assistant streaming", streaming);
        check("voice", "the log says when nothing came after the message", SpokenReply.Describe(SpokenReply.LastRow(new List<ChatMessage> { U("go") }, 0)) == "no row after the message", "");
        return Task.CompletedTask;
    }
}
