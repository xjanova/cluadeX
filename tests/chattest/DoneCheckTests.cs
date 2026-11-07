using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// DC — a file the AI wrote must parse before it may say it is done, and a model hears what it keeps getting wrong with its
/// tools here (owner 2026-11-07, "ทำเรื่องที่คุณเสนอต่อเลย": verify before done + a per-model tool track record). Through the
/// REAL agent loop (CodeAgentService.ExecuteAgenticAsync → the native tool loop → the real write_file in a throwaway
/// project), with a scripted native-tool provider: no network, no model.
/// </summary>
internal static partial class Program
{
    static async Task TestDoneCheck()
    {
        Log("\n## DC — a written file must parse before \"done\"; the tool track record");
        var agent = Sp.GetRequiredService<CodeAgentService>();
        var provider = new NativeScript();
        object before = typeof(AiProviderManager).GetField("_activeProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(Providers)!;
        bool permBefore = Settings.Settings.Features.PermissionSystem;
        bool verifyBefore = Settings.Settings.AutoVerifyAfterEditEnabled;
        string proj = Path.Combine(PipeRoot, "done-check");
        try
        {
            if (Directory.Exists(proj)) Directory.Delete(proj, true);
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "README.md"), "config for the game\n");
            Vm.SetWorkingDirectory(proj);
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = false; s.AutoVerifyAfterEditEnabled = true; });
            SetField(Providers, "_activeProvider", provider);

            static NativeToolResponse Write(string id, string path, string content) => new()
            {
                StopReason = "tool_use",
                ToolCalls = { new NativeToolCall { Id = id, Name = "write_file",
                    Input = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content })).RootElement.Clone() } },
            };
            static NativeToolResponse Say(string text) => new() { StopReason = "end_turn", TextContent = text };

            // ── DC1–DC4: a broken JSON file, a "done" that is refused, the fix, and the finish ──
            provider.Steps.Enqueue((_, _) => Task.FromResult(Write("w1", "config.json", "{\n  \"level\": 3,\n}\n")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Done. config.json is written.")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Write("w2", "config.json", "{\n  \"level\": 3\n}\n")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Fixed: config.json is valid now.")));
            var r = await agent.ExecuteAgenticAsync(new List<ChatMessage>(), "[DC1] write config.json with level 3", null, CancellationToken.None);
            int calls = provider.Texts.Count;
            Check("DC1 right after the step that wrote it, the model is told the JSON does not parse, at its line",
                calls >= 2 && provider.Texts[1].Contains("[file-check] ✗") && provider.Texts[1].Contains("config.json:3"), calls >= 2 ? provider.Texts[1] : $"{calls} calls");
            Check("DC2 its \"done\" is refused while the file is broken, and the next step must be a tool call",
                calls >= 3 && provider.Texts[2].Contains("STOP — do not summarise") && provider.Texts[2].Contains("config.json")
                && provider.Choices.Count >= 3 && provider.Choices[2] == "required", calls >= 3 ? provider.Texts[2] : $"{calls} calls");
            Check("DC3 once fixed, the finish stands with the model's own answer",
                r.Success && r.FinalResponse == "Fixed: config.json is valid now." && calls == 4, $"success={r.Success} calls={calls} final={r.FinalResponse}");
            Check("DC4 the file on disk is the fixed one", File.ReadAllText(Path.Combine(proj, "config.json")).Replace("\r", "") == "{\n  \"level\": 3\n}\n");

            // ── DC5: a model that ignores it is refused twice, never forever (bounded like the red-build guard) ──
            provider.Texts.Clear(); provider.Choices.Clear(); provider.Prompts.Clear();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Write("w3", "data.json", "{ broken")));
            for (int i = 0; i < 3; i++) provider.Steps.Enqueue((_, _) => Task.FromResult(Say("All done.")));
            var r2 = await agent.ExecuteAgenticAsync(new List<ChatMessage>(), "[DC5] write data.json", null, CancellationToken.None);
            int stops = provider.Texts.Count(t => t.StartsWith("STOP — do not summarise", StringComparison.Ordinal));
            Check("DC5 a model that will not fix it is refused twice, then the run ends (never an endless loop)",
                stops == 2 && provider.Texts.Count == 4 && r2.FinalResponse.StartsWith("All done."), $"stops={stops} calls={provider.Texts.Count}");
            Check("DC5 …and the user is told by CluadeX that it is not really done, naming the file and the error",
                r2.FinalResponse.Contains("⚠️ CluadeX checked") && r2.FinalResponse.Contains("`data.json:1`") && r2.FinalResponse.Contains("invalid JSON"),
                r2.FinalResponse);

            // ── DC6: a good file says nothing (no noise for the model) ──
            provider.Texts.Clear(); provider.Choices.Clear(); provider.Prompts.Clear();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Write("w4", "ok.json", "{\"a\": 1}")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("ok.json written.")));
            var r3 = await agent.ExecuteAgenticAsync(new List<ChatMessage>(), "[DC6] write ok.json", null, CancellationToken.None);
            Check("DC6 a file that parses adds no note and the finish stands at once",
                r3.Success && provider.Texts.Count == 2 && !provider.Texts[1].Contains("[file-check]"), $"calls={provider.Texts.Count}");

            // ── DC7: the switch: auto-verify off = no file check either ──
            Settings.UpdateSettings(s => s.AutoVerifyAfterEditEnabled = false);
            provider.Texts.Clear(); provider.Choices.Clear(); provider.Prompts.Clear();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Write("w5", "off.json", "{ broken")));
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("off.json written.")));
            await agent.ExecuteAgenticAsync(new List<ChatMessage>(), "[DC7] write off.json", null, CancellationToken.None);
            Check("DC7 with \"verify after edits\" off, nothing is checked (the owner's switch holds)",
                provider.Texts.Count == 2 && !provider.Texts[1].Contains("[file-check]"), $"calls={provider.Texts.Count}");
            Settings.UpdateSettings(s => s.AutoVerifyAfterEditEnabled = true);

            // ── DC8–DC9: the track record — what this model got wrong is in its next prompt ──
            string label = (string)typeof(CodeAgentService).GetMethod("ModelLabelFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(agent, [provider])!;
            var stats = ToolTrackRecord.Shared.StatsFor(label);
            Check("DC8 every write_file of these runs is in this model's record", stats.TryGetValue("write_file", out var ws) && ws.Calls >= 5,
                $"{label}: {string.Join(", ", stats.Select(kv => $"{kv.Key} {kv.Value.Fails}/{kv.Value.Calls}"))}");
            for (int i = 0; i < 6; i++)
                ToolTrackRecord.Shared.Record(label, "edit_file", false, "Text not found in app.js. Matching is newline- and whitespace-tolerant, but the lines must exist.");
            for (int i = 0; i < 2; i++) ToolTrackRecord.Shared.Record(label, "edit_file", true, null);
            // The record is this data home's file, so an earlier run of the harness has added to it: read what it holds.
            var es = ToolTrackRecord.Shared.StatsFor(label)["edit_file"];
            string expectLine = $"edit_file: failed {es.Fails} of {es.Calls}";
            provider.Texts.Clear(); provider.Choices.Clear(); provider.Prompts.Clear();
            provider.Steps.Enqueue((_, _) => Task.FromResult(Say("Nothing to change.")));
            await agent.ExecuteAgenticAsync(new List<ChatMessage>(), "[DC9] look at README.md", null, CancellationToken.None);
            string prompt = provider.Prompts.FirstOrDefault() ?? "";
            int at = prompt.IndexOf("# Your track record", StringComparison.Ordinal);
            Check("DC9 the next run of that model is told how often edit_file failed here, and to read the exact lines first or write the file whole",
                at >= 0 && prompt.Contains(expectLine) && prompt.Contains("read_file the exact lines"),
                at >= 0 ? prompt.Substring(at, Math.Min(400, prompt.Length - at)) : $"no track record in the prompt (expected '{expectLine}')");
            ToolTrackRecord.Shared.Save();
            Check("DC10 the record is saved in this data home (portable: beside the test, never ~/.cluadex)",
                File.Exists(Path.Combine(Settings.DataRoot, "tool-record.json")));
        }
        finally
        {
            SetField(Providers, "_activeProvider", before);
            Settings.UpdateSettings(s => { s.Features.PermissionSystem = permBefore; s.AutoVerifyAfterEditEnabled = verifyBefore; });
            Vm.SetWorkingDirectory("");
            try { Directory.Delete(proj, true); } catch { }
        }
    }
}
