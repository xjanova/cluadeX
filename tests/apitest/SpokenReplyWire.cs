// Read aloud every reply bubble of a turn, in order, each once (owner 2026-11-11: "ทำไม ไม่อ่านทุกบับเบิ้ลที่ต้องอ่าน"):
// SpokenReply.ToRead picks the bubbles whose text is final (a later bubble or her question follows, or the turn ended)
// and passes over cards, notices, status rows, tool rows and the app's "no summary" line. Before, only the turn's last
// row was read when it ended — a Codex turn with six bubbles (chat 3172b21d, 2026-11-11 10:12) read none by itself.
using CluadeX.Models;
using CluadeX.Services.Voice;

namespace ApiTest;

internal static class SpokenReplyWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · Voice — every reply bubble is read aloud, once, in order");

        static ChatMessage U(string t) => new() { Role = MessageRole.User, Content = t };
        static ChatMessage A(string t) => new() { Role = MessageRole.Assistant, Content = t };
        static ChatMessage Tool() => new() { Role = MessageRole.ToolAction, Content = "read_file a.cs", ToolName = "read_file" };
        static ChatMessage Card(string tag) => new() { Role = MessageRole.System, Content = "{}", ToolName = tag };
        static ChatMessage Ask(string t) => new() { Role = MessageRole.Question, Content = "❓ " + t };
        // What the chat does: the words go to the queue and the bubble remembers them.
        static List<string> Read(List<ChatMessage> rows, int userAt, bool ended)
        {
            var parts = SpokenReply.ToRead(rows, userAt, ended);
            foreach (var p in parts) p.Message.VoiceReadText = p.Message.Content;
            return parts.Select(p => p.Text).ToList();
        }

        // The Codex turn of chat 3172b21d, as its rows came in: a bubble, a notice, tool rows, a plan card, more
        // bubbles between tool calls, then her question (ask_user) while the turn waits.
        var b1 = A("ฉันจะเปิดโปรเจกต์ Thaiprompt แล้วตรวจการเชื่อมต่อ KYC ค่ะ");
        var b2 = A("โฟลเดอร์ยังว่างค่ะบอส ฉันกำลังหา source แอปตัวจริงก่อนค่ะ");
        var b3 = A("พบ source แอปแล้วค่ะ ทั้ง KYC และชวนเพื่อนใช้ API กลางเดียวกัน");
        var b4 = A("ยังทำอาการซ้ำจากเครื่องนี้ไม่ได้ค่ะบอส ต้องรู้เวอร์ชันที่ติดตั้งก่อนค่ะ");
        b4.IsStreaming = true;   // a CLI's text before its ask_user: nothing turned the cursor off
        var rows = new List<ChatMessage> { U("old"), A("old reply"), U("เปิดโปรเจค thaiprompt app มือถือ"), b1 };
        int at = 2;
        check("voice", "the bubble still being written is not read yet", Read(rows, at, false).Count == 0, "");
        rows.Add(new() { Role = MessageRole.System, Content = "📁 เปิดโปรเจกต์: D:\\Code\\App\\thaiprompt" });
        rows.Add(Tool()); rows.Add(Tool());
        check("voice", "a notice or a tool row after a bubble does not read it yet (a CLI can write on in it)",
            Read(rows, at, false).Count == 0, "");
        rows.Add(Card(ChatMessage.TurnChangesTag));
        rows.Add(b2);
        var first = Read(rows, at, false);
        check("voice", "her next bubble reads the one before it, while the turn runs", first.SequenceEqual(new[] { b1.Content }), string.Join(" | ", first));
        rows.Add(Tool()); rows.Add(b3); rows.Add(Tool()); rows.Add(Tool()); rows.Add(b4);
        var next = Read(rows, at, false);
        check("voice", "bubbles are read in the order she wrote them, each once", next.SequenceEqual(new[] { b2.Content, b3.Content }), string.Join(" | ", next));
        rows.Add(Ask("แอปที่บอสใช้เป็นเวอร์ชันอะไรคะ?"));
        var beforeAsk = Read(rows, at, false);
        check("voice", "her question reads the bubble before it at once, even with its cursor still on", beforeAsk.SequenceEqual(new[] { b4.Content }), string.Join(" | ", beforeAsk));
        check("voice", "nothing is read twice", Read(rows, at, false).Count == 0 && Read(rows, at, true).Count == 0, "");

        // A CLI that goes on in the same bubble after the words were read: only what it added is read.
        string said = b4.Content;
        b4.Content = said + " ขอบคุณค่ะ เจอสาเหตุแล้ว";
        rows.Add(A("แก้แล้วค่ะ"));
        var grew = Read(rows, at, true);
        check("voice", "a bubble that grew after it was read: only the new words, then the next bubble",
            grew.SequenceEqual(new[] { " ขอบคุณค่ะ เจอสาเหตุแล้ว", "แก้แล้วค่ะ" }), string.Join(" | ", grew));
        var replaced = A("ก");
        replaced.VoiceReadText = "ข้อความเดิม";
        check("voice", "a bubble whose text was replaced after it was read is not read again", SpokenReply.Unread(replaced) == null, "");

        // When the turn ends: everything not read yet, past the changes card, the next-step box and status rows.
        var final = A("ขึ้น GitHub แล้วค่ะ commit `e5f315e`");
        var ended = new List<ChatMessage>
        {
            U("push"), A("ขอดูก่อนนะคะ"), Tool(), Tool(), final,
            Card(ChatMessage.TurnChangesTag), Card(ChatMessage.NextStepTag),
            new() { Role = MessageRole.AgentStatus, Content = "⏳" },
        };
        var all = Read(ended, 0, true);
        check("voice", "the turn's end reads every bubble not read yet, not only the last", all.SequenceEqual(new[] { "ขอดูก่อนนะคะ", final.Content }), string.Join(" | ", all));

        var typedDuring = new List<ChatMessage> { U("go"), Tool(), A("เสร็จแล้วค่ะ"), U("ขอบคุณ") };
        check("voice", "a note the user typed while she worked does not hide her reply", Read(typedDuring, 0, true).SequenceEqual(new[] { "เสร็จแล้วค่ะ" }), "");

        var noSummary = new List<ChatMessage> { U("go"), A("ขอดูไฟล์ก่อนนะคะ"), Tool(), new() { Role = MessageRole.Assistant, Content = "*ran 2 tools*", IsTurnNote = true } };
        var ns = Read(noSummary, 0, true);
        check("voice", "the app's 'no summary' line is not read; her bubble before the tools is", ns.SequenceEqual(new[] { "ขอดูไฟล์ก่อนนะคะ" }), string.Join(" | ", ns));

        var endsOnTool = new List<ChatMessage> { U("go"), Tool() };
        check("voice", "a turn with no bubble reads nothing", Read(endsOnTool, 0, true).Count == 0, "");

        var folded = new List<ChatMessage> { U("go"), A("ตอบแล้วค่ะ"), new() { Role = MessageRole.Assistant, Content = "opening Grok", FoldedIntoBalloon = true } };
        check("voice", "a line folded into a tool balloon is passed over", Read(folded, 0, true).SequenceEqual(new[] { "ตอบแล้วค่ะ" }), "");

        var bad = new[]
        {
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", HasError = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", IsInterrupted = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", IsStreaming = true },
            new ChatMessage { Role = MessageRole.Assistant, Content = "   " },
            new ChatMessage { Role = MessageRole.Assistant, Content = "x", ToolName = "run_command" },
        };
        check("voice", "an error, a stopped reply, one cut off still streaming, an empty one or a tool row is not read",
            bad.All(b => SpokenReply.ToRead(new List<ChatMessage> { U("go"), b }, 0, true).Count == 0), "");
        var cutAfter = new List<ChatMessage> { U("go"), A("ขอดูก่อนนะคะ"), Tool(), new() { Role = MessageRole.Assistant, Content = "กำลังเขียน", IsStreaming = true } };
        var cut = Read(cutAfter, 0, true);
        check("voice", "a bubble before the cut-off one is still read", cut.SequenceEqual(new[] { "ขอดูก่อนนะคะ" }), string.Join(" | ", cut));
        check("voice", "a missing user row reads nothing",
            SpokenReply.ToRead(ended, -1, true).Count == 0 && SpokenReply.ToRead(ended, 99, true).Count == 0, "");

        // A bubble before a tool now is read: the text tool protocol's call in it is not.
        string spoken = SpeechText.ForSpeech("ขอดูไฟล์ก่อนนะคะ\n[ACTION: read_file]\npath: a.cs\n[/ACTION]\nแล้วจะสรุปให้ค่ะ", true);
        check("voice", "the [ACTION:] call written into a bubble is not read aloud, her words around it are",
            spoken.Contains("ขอดูไฟล์ก่อนนะคะ") && spoken.Contains("แล้วจะสรุปให้ค่ะ") && !spoken.Contains("ACTION") && !spoken.Contains("a.cs"), spoken);
        string cutCall = SpeechText.ForSpeech("ขอดูก่อนค่ะ [ACTION: run_command]\ncommand: dir", true);
        check("voice", "…nor a call still being written (no [/ACTION] yet)", cutCall.TrimEnd('.', ' ') == "ขอดูก่อนค่ะ", cutCall);

        // The Voice log line says why a turn was not read.
        string why = SpokenReply.Describe(SpokenReply.LastRow(new List<ChatMessage> { U("go"), A("x"), Tool() }, 0));
        check("voice", "the log names the row a silent turn ended on", why == "ToolAction read_file", why);
        string streaming = SpokenReply.Describe(SpokenReply.LastRow(new List<ChatMessage> { U("go"), bad[2] }, 0));
        check("voice", "the log says the reply was still streaming", streaming == "Assistant streaming", streaming);
        check("voice", "the log says when nothing came after the message", SpokenReply.Describe(SpokenReply.LastRow(new List<ChatMessage> { U("go") }, 0)) == "no row after the message", "");
        return Task.CompletedTask;
    }
}
