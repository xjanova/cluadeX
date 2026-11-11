using CluadeX.Models;
using CluadeX.Services.Voice.Tts;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>VB — every reply bubble of a turn is read aloud, in order, once (owner 2026-11-11: "ทำไม ไม่อ่านทุก
    /// บับเบิ้ลที่ต้องอ่าน"): a real agent turn writes a bubble, asks the user (ask_user), and answers after. Before, only
    /// the last row was read when the turn ended, so the bubble before her question was never read — and nothing was
    /// read while she waited for the answer. A silent output stands in for the speaker (no network, no sound).</summary>
    static async Task TestVoiceEveryBubble()
    {
        Log("\n## VB — every reply bubble is read aloud, in order, once — while the turn runs");
        Vm.SetWorkingDirectory(ProjA);
        bool agenticBefore = Vm.AgenticMode;
        Vm.AgenticMode = true;
        await FreshChat();
        var queue = Vm.VoiceQueue;
        var speakerBefore = queue.Output;
        var silent = new SilentVoiceOutput();
        queue.Output = silent;
        Settings.UpdateSettings(s => s.VoiceSpeakReplies = true);
        try
        {
            int itemsBefore = queue.Items.Count;
            Fake.ScriptFor["[VB]"] = new Queue<FakeStep>([FakeStep.Reply("ขอดูไฟล์ก่อนนะคะ ต้องถามบอสหนึ่งเรื่องค่ะ\n[ACTION: ask_user]\nquestion: ใช้ฐานข้อมูลไหนคะ?\noptions: SQLite, Postgres\n[/ACTION]")]);
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Reply("ตั้งค่า SQLite ให้แล้วค่ะ เสร็จเรียบร้อย"));
            await Send("[VB] ตั้งค่าฐานข้อมูล");
            ChatMessage? q = null;
            bool asked = await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 20000);
            bool readWhileAsking = await WaitUntil(() => queue.Items.Skip(itemsBefore).Any(i => i.Text.Contains("ขอดูไฟล์ก่อนนะคะ")), 5000);
            string while_ = string.Join(" | ", queue.Items.Skip(itemsBefore).Select(i => i.Text));
            Check("VB1 her bubble before ask_user is read while the question waits (the turn still runs)",
                asked && readWhileAsking && Vm.IsGenerating && !while_.Contains("ใช้ฐานข้อมูลไหน"), while_);
            Check("VB1 …its words only — not the ask_user call written after them", !while_.Contains("ACTION") && !while_.Contains("options"), while_);
            if (q == null) return;
            q.AnswerQuestion("SQLite");
            bool done = await WaitIdle(20000);
            await Task.Delay(300);
            var said = queue.Items.Skip(itemsBefore).Select(i => i.Text).ToList();
            Check("VB2 when the turn ends her answer is read after it: both bubbles, in order, each once",
                done && said.Count == 2 && said[0].Contains("ขอดูไฟล์ก่อนนะคะ") && said[1].Contains("ตั้งค่า SQLite ให้แล้วค่ะ"), string.Join(" | ", said));
            Check("VB2 …the voice played them one after another (a new one waits, never cuts)",
                silent.Started.Count >= 1 && silent.Started[0].Text.Contains("ขอดูไฟล์ก่อนนะคะ"), string.Join(" | ", silent.Started.Select(i => i.Text)));
        }
        finally
        {
            Settings.UpdateSettings(s => s.VoiceSpeakReplies = false);
            queue.Output = speakerBefore;
            Vm.AgenticMode = agenticBefore;
            Fake.ScriptFor.Clear();
        }
    }

    /// <summary>The voice queue's sound, silent: records what it was asked to play and ends each at once.</summary>
    private sealed class SilentVoiceOutput : IReplyVoiceOutput
    {
        public readonly List<SpokenItem> Started = new();
        public event Action<SpokenItem, string?>? Ended;
        public bool CanPause => false;
        public Task PrepareAsync(SpokenItem item, CancellationToken ct) => Task.CompletedTask;
        public bool Start(SpokenItem item)
        {
            Started.Add(item);
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Ended?.Invoke(item, null));
            return true;
        }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
    }
}
