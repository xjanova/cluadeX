using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Safety;
using CluadeX.Services.Voice;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>The chimes this run rang (AttentionChime.Sink, set at start-up: no sound from the speakers in a test).</summary>
    static readonly List<ChimeKind> Chimes = new();

    /// <summary>CH — a "ting" when a box waits for the owner's answer (owner 2026-11-11: "มีเอฟเฟคเสียง ติ๊ง เมื่อต้องตอบ
    /// คำถามจากกล่อง หรือกล่องแดง แจ้งเตือนให้เหมาะสม"): her question card and a permission prompt ring the soft one, the red
    /// prompt the alert; several at once ring once; Settings can turn it off.</summary>
    static async Task TestAttentionChime()
    {
        Log("\n## CH — a chime when a box waits for the owner's answer");
        await FreshChat();
        Settings.UpdateSettings(s => s.AttentionChime = true);
        AttentionChime.Reset();
        Chimes.Clear();
        var permHandler = (Func<PermissionRequestInfo, Task<bool>>)typeof(AgentToolService)
            .GetField("OnPermissionRequired", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Sp.GetRequiredService<AgentToolService>())!;
        Task<string> Ask(string q) => (Task<string>)typeof(ChatViewModel).GetMethod("AskUserAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Vm, [q, new List<string> { "A", "B" }])!;
        // Still open: a question not answered, a permission prompt not resolved (QuestionAnswered is true on any other row).
        ChatMessage? Open(Func<ChatMessage, bool> what) => Vm.Messages.LastOrDefault(m => what(m)
            && (m.Role == MessageRole.Question ? !m.QuestionAnswered : !m.PermissionAnswered));

        // her question
        var askTask = Ask("Which one?");
        ChatMessage? q = null;
        await WaitUntil(() => (q = Open(m => m.Role == MessageRole.Question)) != null, 5000);
        Check("CH1 her question card rings the soft ting, once", q != null && Chimes.SequenceEqual(new[] { ChimeKind.Ask }), string.Join(",", Chimes));
        q?.AnswerQuestion("A");
        await Task.WhenAny(askTask, Task.Delay(3000));

        // the red prompt, right after: its own sound, not held back by the ting before it
        var dangerTask = DangerApproval.Handler!(new DangerRequest("run_command", "rd /s /q C:\\Users\\me", "deletes your home folder"));
        ChatMessage? red = null;
        await WaitUntil(() => (red = Open(m => m.IsPermissionDanger)) != null, 5000);
        Check("CH2 the red prompt rings the alert, even a moment after a ting", red != null && Chimes.LastOrDefault() == ChimeKind.Danger && Chimes.Count == 2,
            string.Join(",", Chimes));
        red?.ResolvePermission(PermissionOutcome.Denied);
        await Task.WhenAny(dangerTask, Task.Delay(3000));

        // a permission prompt (not red), and two more at once: one ting for the lot
        AttentionChime.Reset();
        Chimes.Clear();
        var p1 = permHandler(new PermissionRequestInfo { ToolName = "run_command", Detail = "npm test", AlwaysAsk = true });
        var p2 = permHandler(new PermissionRequestInfo { ToolName = "run_command", Detail = "npm run lint", AlwaysAsk = true });
        await WaitUntil(() => Vm.Messages.Count(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered) == 2, 5000);
        await Task.Delay(200);
        Check("CH3 a permission prompt rings the soft ting; two at once ring once", Chimes.SequenceEqual(new[] { ChimeKind.Ask }), string.Join(",", Chimes));
        foreach (var m in Vm.Messages.Where(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered).ToList())
            m.ResolvePermission(PermissionOutcome.Denied);
        await Task.WhenAny(Task.WhenAll(p1, p2), Task.Delay(3000));

        // turned off in Settings
        Settings.UpdateSettings(s => s.AttentionChime = false);
        AttentionChime.Reset();
        Chimes.Clear();
        var quiet = Ask("Quiet one?");
        ChatMessage? q2 = null;
        await WaitUntil(() => (q2 = Open(m => m.Role == MessageRole.Question)) != null, 5000);
        await Task.Delay(200);
        Check("CH4 with the chime off in Settings, a box rings nothing", q2 != null && Chimes.Count == 0, string.Join(",", Chimes));
        q2?.AnswerQuestion("B");
        await Task.WhenAny(quiet, Task.Delay(3000));

        // Settings › General › Appearance: the switch is bound, and turning it on rings it once to hear it
        var svm = Sp.GetRequiredService<SettingsViewModel>();
        svm.AttentionChime = true;
        Check("CH5 turning it on in Settings saves it and rings the ting once", Settings.Settings.AttentionChime && Chimes.SequenceEqual(new[] { ChimeKind.Ask }),
            string.Join(",", Chimes));
        Chimes.Clear();
    }
}
