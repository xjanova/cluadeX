// The pipe run's reply (Reverse X phase 3, 2026-10-13): after 60+ steps of real work the brain-first guard fired, the
// model searched and answered "the summary above stands", and that one line was all the caller got back. The guard now
// fires only on an answer given with no tool used; if it does fire, a short remark no longer replaces the answer.
// Also the savings estimate's token count of what microcompact kept out of a request.
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class AgentReplyWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · the reply a run returns after the brain-first guard, and microcompact's kept-out tokens");

        string summary = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"- did thing {i}: files, tests, results"));
        const string remark = "Searched the brain afterwards — the summary above stands.";
        string? kept = CodeAgentService.KeepAnswerOverRemark(summary, remark);
        check("reply", "a short remark after the guard keeps the full answer, with the remark under it",
            kept != null && kept.StartsWith(summary) && kept.EndsWith(remark), kept ?? "null");
        string fullAgain = summary + "\n- and the note it relied on: X";
        check("reply", "an answer given again in full replaces the earlier one", CodeAgentService.KeepAnswerOverRemark(summary, fullAgain) == fullAgain, "");
        check("reply", "no guard (no earlier answer): the final answer as it is", CodeAgentService.KeepAnswerOverRemark(null, remark) == remark, "");
        check("reply", "an empty final answer falls back to the earlier one", CodeAgentService.KeepAnswerOverRemark(summary, "  ") == summary, "");

        var msgs = new List<NativeMessage>();
        for (int i = 0; i < 6; i++)
        {
            msgs.Add(new NativeMessage { Role = "assistant", Content = { new ContentBlock { Type = "text", Text = "reading file " + i } } });
            msgs.Add(new NativeMessage { Role = "user", Content = { new ContentBlock { Type = "tool_result", ToolUseId = "t" + i, Content = new string('x', 8_000) } } });
        }
        var shortened = CodeAgentService.MicrocompactUpTo(msgs, 8, 800);
        int before = CodeAgentService.EstimateNativeTokens(msgs), after = CodeAgentService.EstimateNativeTokens(shortened);
        check("savings", "microcompact's kept-out tokens: four 8,000-character results cut to ~800 save roughly 4 × 1.8k tokens",
            before - after is > 5_000 and < 8_500, $"{before} → {after} ({before - after} kept out)");
        check("savings", "negative control: nothing before the boundary, nothing kept out",
            CodeAgentService.EstimateNativeTokens(CodeAgentService.MicrocompactUpTo(msgs, 0, 800)) == before, "");
        return Task.CompletedTask;
    }
}
