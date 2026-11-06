using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Providers;
using CluadeX.Services.Safety;

namespace SafetyTest;

/// <summary>
/// Grok Dev's guard, for real: the turn home GrokTurnHome builds (junctions included, and that deleting it leaves the
/// folders they point at), the real CluadeX.exe guard and its guard.cmd wrapper fed Grok-shaped hook input, and the REAL
/// installed Grok Build CLI reading that home (`grok inspect`: our hook, our MCP server, our read-only agent). Nothing here
/// signs in or reaches a model: `grok inspect` is local, and the "user's home" is a throwaway folder.
/// What `grok inspect` taught (1.0.70, 2026-11-06): a config.toml that does not parse switches every hook off ("fail
/// closed"); hooks show only for a trusted folder, so it runs from the user's profile (always trusted), as `grok -p` counts
/// any folder trusted for the user's own hooks; GROK_AUTH_PATH is read (its log names the path).
/// </summary>
internal static class GrokCliReal
{
    private static readonly string ForcedPush = "git push" + " --force origin main";

    public static void Run(string work, Action<string, bool, string> check, Func<string?> matchingExe)
    {
        Console.WriteLine("Grok Dev: the turn home, the real guard on Grok's input, the real Grok CLI reading the home");
        string fakeReal = Path.Combine(work, "grok-user");
        Directory.CreateDirectory(Path.Combine(fakeReal, "hooks"));
        Directory.CreateDirectory(Path.Combine(fakeReal, "agents"));
        Directory.CreateDirectory(Path.Combine(fakeReal, "memory"));
        Directory.CreateDirectory(Path.Combine(fakeReal, "sessions"));
        File.WriteAllText(Path.Combine(fakeReal, "config.toml"), "[mcp_servers.my-own]\ncommand = 'cmd.exe'\nargs = ['/c', 'exit']\n");
        File.WriteAllText(Path.Combine(fakeReal, "auth.json"), """{"default":{"refresh_token":"r"}}""");
        File.WriteAllText(Path.Combine(fakeReal, "AGENTS.md"), "user rules");
        File.WriteAllText(Path.Combine(fakeReal, "hooks", "mine.json"), """{"hooks":{}}""");
        File.WriteAllText(Path.Combine(fakeReal, "agents", "mine.md"), "---\nname: mine\ndescription: mine\n---\nhi\n");
        File.WriteAllText(Path.Combine(fakeReal, "memory", "keep.txt"), "the user's memory");
        File.WriteAllText(Path.Combine(fakeReal, "sessions", "old.sqlite"), "x");

        var home = GrokTurnHome.At(Path.Combine(work, "grok-turn", "t1"));
        home.CarryUserHome(fakeReal);
        string bridge = CluadeX.Services.Tools.ChatToolBridge.ConfigJson(@"C:\x\CluadeX.exe", "tok", @"\\.\pipe\cx");
        bool chat = home.WriteConfig(fakeReal, bridge);
        string exe = matchingExe() ?? Path.Combine(work, "CluadeX.exe");
        home.WriteHooks(AntigravityDevProvider.ShortPath(exe), noEdit: true);

        string H(params string[] p) => Path.Combine(new[] { home.Home }.Concat(p).ToArray());
        bool memLinked = new DirectoryInfo(H("memory")).Attributes.HasFlag(FileAttributes.ReparsePoint);
        check("grok home: memory is a junction to the user's own; sessions are not carried; the sign-in is not copied",
            memLinked && File.Exists(H("memory", "keep.txt")) && !Directory.Exists(H("sessions")) && !File.Exists(H("auth.json"))
            && home.UserAuthFile == Path.Combine(fakeReal, "auth.json"), $"linked={memLinked}");
        check("grok home: AGENTS.md copied; the user's hook file and agent copied beside ours",
            File.Exists(H("AGENTS.md")) && File.Exists(H("hooks", "mine.json")) && File.Exists(H("hooks", GrokTurnHome.HooksFileName))
            && File.Exists(H("agents", "mine.md")) && File.Exists(H("agents", GrokTurnHome.ExploreAgent + ".md")), "");
        string cfg = File.ReadAllText(H("config.toml"));
        check("grok home: config.toml = the user's own, then [mcp_servers.cluadex]",
            chat && cfg.StartsWith("[mcp_servers.my-own]") && cfg.Contains("[mcp_servers.cluadex]"), cfg);
        check("grok home: the user's own hooks and agents folders were not written to",
            Directory.GetFiles(Path.Combine(fakeReal, "hooks")).Length == 1 && Directory.GetFiles(Path.Combine(fakeReal, "agents")).Length == 1, "");

        GuardProcess(home, exe, matchingExe() != null, check);
        RealCli(home, check);

        home.Delete();
        check("grok home: deleting the turn's home unlinks the junction and leaves the user's memory in place",
            !Directory.Exists(home.Home) && File.Exists(Path.Combine(fakeReal, "memory", "keep.txt")), "");
    }

    /// <summary>The real guard on Grok's hook input: straight, and through the guard.cmd the turn writes.</summary>
    private static void GuardProcess(GrokTurnHome home, string exe, bool haveExe, Action<string, bool, string> check)
    {
        if (!haveExe) return;
        string cwd = Path.GetDirectoryName(home.Home)!;
        (int Code, string Err) Ask(string fileName, string json, bool noEdit)
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(GuardHook.Flag);
            if (noEdit) psi.Environment[GuardHook.NoEditEnv] = "1";
            psi.Environment.Remove(GuardHook.AgentGateTokenEnv);
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(json);
            p.StandardInput.Close();
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); return (-1, "timeout"); }
            return (p.ExitCode, err);
        }
        // Grok's envelope: camelCase, with Claude's snake_case beside it for the tool keys.
        static string Grok(string tool, object input, string dir, string? subagent = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hookEventName"] = "pre_tool_use", ["hook_event_name"] = "PreToolUse", ["sessionId"] = "s", ["cwd"] = dir,
            ["toolName"] = tool, ["toolInput"] = input, ["tool_name"] = tool, ["tool_input"] = input, ["subagentType"] = subagent,
        });
        string camelOnly = JsonSerializer.Serialize(new { hookEventName = "pre_tool_use", cwd, toolName = "run_terminal_cmd", toolInput = new { command = ForcedPush } });

        var ok = Ask(exe, Grok("run_terminal_cmd", new { command = "echo hi" }, cwd), false);
        check("grok guard: a harmless run_terminal_cmd passes (exit 0)", ok.Code == 0, $"{ok.Code} {ok.Err}");
        var never = Ask(exe, camelOnly, false);
        check("grok guard: a force push in camelCase-only input is refused outright (exit 2)", never.Code == 2 && never.Err.Contains("never run"), $"{never.Code} {never.Err}");
        var write = Ask(exe, Grok("write", new { path = Path.Combine(cwd, "b.cs"), content = "class B { }" }, cwd), true);
        check("grok guard: Grok's write in a run granted commands only is refused (exit 2)", write.Code == 2 && write.Err.Contains("not edit"), $"{write.Code} {write.Err}");
        var edit = Ask(exe, Grok("search_replace", new { file_path = Path.Combine(cwd, "b.cs"), old_string = "B", new_string = "C" }, cwd), false);
        check("grok guard: …the same kind of edit passes in a run that may edit", edit.Code == 0, $"{edit.Code} {edit.Err}");
        var sub = Ask(exe, Grok("search_replace", new { file_path = Path.Combine(cwd, "b.cs"), old_string = "B", new_string = "C" }, cwd, GrokTurnHome.ExploreAgent), false);
        check("grok guard: an edit from inside cluadex-explore is refused", sub.Code == 2 && sub.Err.Contains(GrokTurnHome.ExploreAgent), $"{sub.Code} {sub.Err}");
        var iso = Ask(exe, Grok("spawn_subagent", new { prompt = "x", isolation = "worktree" }, cwd), false);
        check("grok guard: a spawn in its own worktree is refused (it would live in the deleted turn home)", iso.Code == 2 && iso.Err.Contains("worktree"), $"{iso.Code} {iso.Err}");
        var spawn = Ask(exe, Grok("spawn_subagent", new { prompt = "x", subagent_type = GrokTurnHome.ExploreAgent }, cwd), false);
        check("grok guard: a plain spawn with no gate behind the run goes through", spawn.Code == 0, $"{spawn.Code} {spawn.Err}");

        // guard.cmd as Grok starts it: no shell, the wrapper sets the turn dir and no-edit, and turns failure into exit 2.
        string wrapper = Path.Combine(home.Home, "guard.cmd");
        var wOk = Ask(wrapper, Grok("run_terminal_cmd", new { command = "echo hi" }, cwd), false);
        var wPush = Ask(wrapper, Grok("run_terminal_cmd", new { command = ForcedPush }, cwd), false);
        var wWrite = Ask(wrapper, Grok("write", new { path = Path.Combine(cwd, "b.cs"), content = "x" }, cwd), false);
        var wSelf = Ask(wrapper, Grok("run_terminal_cmd", new { command = "del \"" + wrapper + "\"" }, cwd), false);
        check("grok guard.cmd: passes a harmless command, refuses a force push, a write (no-edit run) and touching its own folder",
            wOk.Code == 0 && wPush.Code == 2 && wWrite.Code == 2 && wSelf.Code == 2 && wSelf.Err.Contains("GROK_HOME"),
            $"ok={wOk.Code} push={wPush.Code} write={wWrite.Code} self={wSelf.Code} {wSelf.Err}");
    }

    /// <summary>The installed Grok CLI reads the home: <c>grok inspect</c> (local, no sign-in, no model).</summary>
    private static void RealCli(GrokTurnHome home, Action<string, bool, string> check)
    {
        string? grok = GrokDevProvider.Locate();
        if (grok == null) { Console.WriteLine("  SKIP the Grok Build CLI is not installed here"); return; }
        var psi = new ProcessStartInfo(grok)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("inspect");
        psi.Environment["GROK_HOME"] = home.Home;
        psi.Environment["GROK_AUTH_PATH"] = Path.Combine(home.Home, "no-sign-in.json");
        psi.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment.Remove("XAI_API_KEY");
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var errTask = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(90000)) { try { p.Kill(true); } catch { } }
        string err = errTask.GetAwaiter().GetResult();
        check("grok inspect: the turn's config.toml parses (a parse error switches every hook off)",
            text.Contains(Path.Combine(home.Home, "config.toml")) && !text.Contains("(parse error)"), Tail(text + err));
        check("grok inspect: the guard's PreToolUse hook is loaded, with its matcher", text.Contains("matcher=" + GrokTurnHome.GuardMatcher), Tail(text));
        check("grok inspect: CluadeX's MCP server sits beside the user's own", text.Contains("cluadex (stdio)") && text.Contains("my-own (stdio)"), Tail(text));
        check("grok inspect: the read-only sub-agent type is offered", text.Contains(GrokTurnHome.ExploreAgent), Tail(text));
    }

    private static string Tail(string s) => s.Length > 1500 ? s[^1500..] : s;
}
