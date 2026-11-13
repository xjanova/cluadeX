// When CluadeX offers a new chat for a task (owner 2026-11-13: "ถ้าคำสั่งที่เข้ามากลางแชทมันคนละ effort หรือควรเปลี่ยนโมเดล
// ก็แยกแชทไปดีกว่าไหม"). The rule is pure: a message that reads as a task of its own, in a chat that is already long.
// A wrong "yes" costs the owner a click every time, so the rule is conservative — these are the cases it must get right.
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace ApiTest;

internal static class NewChatAdviceWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · offering a new chat for a task of its own");
        int big = NewChatAdvice.BigChatTokens;

        // ─── a task of its own ───
        foreach (string task in new[]
        {
            "เพิ่มหน้ารายงานยอดขายรายเดือนพร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard",
            "implement a CSV export for the monthly sales report page, with a download button",
            "วางแผน refactor ระบบ provider ทั้งระบบให้รองรับค่ายใหม่ แล้วเขียนเป็นเอกสารสั้นๆ",
            "หน้า login ของเว็บร้านค้าเปิดไม่ได้ ขึ้น error 500 ตอนกดเข้าสู่ระบบ ช่วยหาสาเหตุแล้วแก้ให้ที",
        })
            check("new-chat", $"a task of its own: \"{task[..Math.Min(48, task.Length)]}…\"", NewChatAdvice.IsOwnTask(task), "");

        // ─── about the work in the chat: stays where it is ───
        foreach (var (msg, why) in new[]
        {
            ("ต่อเลย", "a go-ahead"),
            ("แก้เลยค่ะ", "a go-ahead"),
            ("ทำไม build ช้าจัง อธิบายหน่อยว่าขั้นไหนกินเวลามากที่สุดในโปรเจกต์นี้", "a question"),
            ("ขอบคุณมากค่ะ ใช้ได้แล้ว", "short chat"),
            ("แก้อันเมื่อกี้ให้ปุ่มเป็นสีเขียว และเพิ่มข้อความกำกับใต้ปุ่มให้ชัดขึ้นด้วย", "points at what was just done"),
            ("เพิ่มเทสต์ให้ฟังก์ชันที่เพิ่งแก้ไปด้วย แล้วรันดูว่าผ่านครบทุกกรณีไหม", "points at what was just done"),
            ("ยังไม่หาย ปุ่มยังไม่ทำงานเหมือนเดิม แก้ใหม่อีกรอบแล้วเช็กให้ละเอียดกว่าเดิม", "the fix made here did not hold"),
            ("update the fix you made above so it also handles an empty list, then add a test", "points at the conversation"),
            ("continue with the remaining files and run the build when you are done with them", "carries on"),
            ("/commit ทุกไฟล์ที่แก้ไว้ แล้วเขียนข้อความ commit ให้ครบตามที่ทำมา", "a slash command"),
            ("เพิ่มปุ่ม Export", "too short to be a task of its own"),
        })
            check("new-chat", $"stays in the chat ({why}): \"{msg[..Math.Min(44, msg.Length)]}\"", !NewChatAdvice.IsOwnTask(msg), "");

        // ─── only what the owner wrote is read ───
        string task1 = "เพิ่มหน้ารายงานยอดขายรายเดือนพร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard";
        string recall = "<brainx_recall_ab12>\nprevious work above: you just fixed the export, continue\n</brainx_recall_ab12>\n\n";
        check("new-chat", "a recall block's words are not the owner's: the task under it still stands by itself", NewChatAdvice.IsOwnTask(recall + task1), "");

        // ─── the chat must be long already ───
        check("new-chat", "a task of its own in a short chat is not asked about; in a long one it is",
            !NewChatAdvice.ForBigChat(task1, big - 1) && NewChatAdvice.ForBigChat(task1, big) && NewChatAdvice.ForBigChat(task1, big * 3), $"threshold {big}");
        check("new-chat", "a follow-up in a long chat is not asked about either", !NewChatAdvice.ForBigChat("แก้อันเมื่อกี้ให้ปุ่มเป็นสีเขียว และเพิ่มข้อความกำกับใต้ปุ่มให้ชัดขึ้นด้วย", big * 3), "");
        check("new-chat", "another model: asked only in a long chat that has spoken",
            NewChatAdvice.ForModelSwitch(big, true) && !NewChatAdvice.ForModelSwitch(big - 1, true) && !NewChatAdvice.ForModelSwitch(big * 2, false), "");

        // ─── a waiting message taken back from the run ───
        var run = new AgentRunScope("chat", null);
        object a = new(), b = new(), c = new();
        run.AddUserNote("first", a);
        run.AddUserNote("second", b);
        run.AddUserNote("third", c);
        bool took = run.WithdrawUserNote(b), again = run.WithdrawUserNote(b), stranger = run.WithdrawUserNote(new object());
        var left = run.DrainUserNotes();
        check("new-chat", "a waiting message is taken out of the run's queue once; the others keep their order",
            took && !again && !stranger && left.Count == 2 && left[0].Text == "first" && left[1].Text == "third",
            $"took={took} again={again} left={string.Join(",", left.Select(n => n.Text))}");
        check("new-chat", "a message the agent has read already cannot be taken back", !run.WithdrawUserNote(a) && !run.HasUserNotes, "");
        return Task.CompletedTask;
    }
}
