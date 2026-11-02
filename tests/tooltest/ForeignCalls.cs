using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Desktop;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;

namespace ToolTest;

/// <summary>
/// Tools for every model (owner 2026-11-02: "เจ้าอื่น เงอะงะ ผิดพลาดรันไม่ค่อยได้"): a call phrased the way Gemini, GPT,
/// Qwen or a Claude-trained local model writes it — Read / replace / functions.read_file, file_path / old_string /
/// search — runs as CluadeX's own tool, and a call that cannot be understood is refused instead of guessed at (an
/// edit_file with search+replace used to APPEND the replacement to the end of the file). Plus the desktop tools the
/// CLI agents now get through CluadeX's MCP bridge.
/// </summary>
internal static partial class Program
{
    /// <summary>A call as the native loop builds it: the type from the resolver, the name translated to CluadeX's.</summary>
    static async Task<ToolResult> TN(string tool, params (string k, string v)[] args)
    {
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = Svc.CanonicalToolName(tool), Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Svc.ExecuteToolAsync(call, cts.Token);
    }

    static async Task ForeignCalls()
    {
        Directory.CreateDirectory(P("Foreign"));
        File.WriteAllText(P("Foreign", "notes.txt"), "alpha\nbeta\ngamma\n");
        const string rel = "Foreign/notes.txt";

        await Case("FN1", async () =>
        {
            var r = await TN("Read", ("file_path", rel));
            Check("FN1", "Claude-style Read + file_path reads the file", r.Success && Seen(r).Contains("beta"), Seen(r));
        });
        await Case("FN2", async () =>
        {
            var r = await TN("functions.read_file", ("path", rel));
            Check("FN2", "a 'functions.' prefix (GPT / Qwen) still names read_file", r.Success && Seen(r).Contains("gamma"), Seen(r));
            Check("FN2b", "the call runs under CluadeX's own name", Svc.CanonicalToolName("functions.read_file") == "read_file"
                                                                       && Svc.CanonicalToolName("ReadFile") == "read_file");
        });
        await Case("FN3", async () =>
        {
            var r = await TN("replace", ("file_path", rel), ("old_string", "beta"), ("new_string", "BETA"));
            string now = Read(rel);
            Check("FN3", "Gemini's replace(file_path, old_string, new_string) edits in place", r.Success && now == "alpha\nBETA\ngamma\n", Seen(r) + "\n--- file:\n" + now);
        });
        await Case("FN4", async () =>
        {
            var r = await TN("edit_file", ("path", rel), ("search", "gamma"), ("replace", "GAMMA"));
            string now = Read(rel);
            Check("FN4", "edit_file with search= instead of find= replaces, never appends", r.Success && now == "alpha\nBETA\nGAMMA\n", Seen(r) + "\n--- file:\n" + now);
        });
        await Case("FN5", async () =>
        {
            string before = Read(rel);
            var r = await TN("edit_file", ("path", rel), ("pattern", "alpha"), ("replace", "OMEGA"));
            string now = Read(rel);
            Check("FN5", "edit_file with an unknown key for the old text is refused and the file is untouched",
                !r.Success && now == before && Seen(r).Contains("pattern"), Seen(r) + "\n--- file:\n" + now);
        });
        await Case("FN6", async () =>
        {
            var r = await TN("write_file", (ToolCallNormalizerKey, "{\"path\": \"x.txt\", \"content\": \"unterminated"));
            Check("FN6", "arguments that were not a JSON object are refused, nothing written",
                !r.Success && Seen(r).Contains("not a valid JSON object") && !File.Exists(P("x.txt")), Seen(r));
        });
        await Case("FN7", async () =>
        {
            var lenient = ApiProviderBase.ParseArgumentsText("{\"path\": \"a.txt\", /* note */ \"limit\": 5,}");
            var broken = ApiProviderBase.ParseArgumentsText("[\"a.txt\"]");
            Check("FN7", "argument JSON with comments and a trailing comma still parses",
                lenient.ValueKind == JsonValueKind.Object && lenient.TryGetProperty("path", out var pth) && pth.GetString() == "a.txt"
                && lenient.TryGetProperty("limit", out var lim) && lim.GetInt32() == 5, lenient.GetRawText());
            Check("FN7b", "an array where an object belongs is marked invalid, not run",
                broken.ValueKind == JsonValueKind.Object && broken.TryGetProperty(ToolCallNormalizerKey, out _), broken.GetRawText());
            await Task.CompletedTask;
        });
        await Case("FN8", async () =>
        {
            string longName = "mcp__a-very-long-server-key-for-testing__" + new string('t', 40);
            string wire = ApiProviderBase.WireToolName(longName);
            Check("FN8", "an MCP name over 64 characters goes out as a valid alias",
                wire.Length <= 64 && wire != longName && System.Text.RegularExpressions.Regex.IsMatch(wire, "^[A-Za-z_][A-Za-z0-9_-]*$"), wire);
            Check("FN8b", "the alias maps back to the real name", ApiProviderBase.OriginalToolName(wire) == longName
                                                                  && Svc.CanonicalToolName(wire) == longName);
            Check("FN8c", "a name that fits is sent as it is", ApiProviderBase.WireToolName("read_file") == "read_file");
            await Task.CompletedTask;
        });
        await Case("FN9", async () =>
        {
            var r = await TN("Grep", ("pattern", "BETA"), ("path", "Foreign"));
            Check("FN9", "Claude-style Grep finds text", r.Success && Seen(r).Contains("notes.txt"), Seen(r));
        });

        // ── desktop tools for every CLI agent (no input is sent: these read lists and text only) ──
        await Case("DK1", async () =>
        {
            var names = DesktopToolModule.ToolNames;
            Check("DK1", "the desktop module has desktop_mouse, desktop_window, desktop_read, desktop_clipboard",
                new[] { "desktop_mouse", "desktop_window", "desktop_read", "desktop_clipboard" }.All(names.Contains), string.Join(", ", names));
            Check("DK1b", "the new ToolTypes are appended after CopyFile (stored by number)",
                (int)ToolType.DesktopMouse == (int)ToolType.CopyFile + 1 && (int)ToolType.DesktopClipboard == (int)ToolType.CopyFile + 4);
            Check("DK1c", "every name has a schema", DesktopToolModule.Schemas().Select(s => s.Name).OrderBy(n => n).SequenceEqual(names.OrderBy(n => n)));
            await Task.CompletedTask;
        });
        await Case("DK2", async () =>
        {
            Check("DK2", "the CLI bridge offers every desktop tool (mcp__cluadex__desktop_*)",
                DesktopToolModule.ToolNames.All(n => ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__" + n)));
            var list = JsonSerializer.SerializeToElement(ChatToolBridge.ToolList());
            var read = list.EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == "desktop_read");
            var mouse = list.EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == "desktop_mouse");
            Check("DK2b", "tools/list carries desktop_read as read-only and desktop_mouse as not",
                read.ValueKind == JsonValueKind.Object && read.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()
                && mouse.ValueKind == JsonValueKind.Object && !mouse.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            string on = ChatToolBridge.DesktopHint(null, claudeNames: true), off = ChatToolBridge.DesktopHint("Desktop control is switched off.");
            Check("DK2c", "the wrapper line names the tools, or says why they are unavailable",
                on.Contains("mcp__cluadex__desktop_mouse") && !on.Contains('\n') && off.Contains("unavailable") && off.Contains("switched off"), on + "\n" + off);
            await Task.CompletedTask;
        });
        await Case("DK3", async () =>
        {
            var blocked = new[] { "KeePass*", "1Password.exe" };
            Check("DK3", "blocked apps: wildcard and .exe forms match, other apps do not",
                DesktopSafety.BlockedAppRefusal("KeePassXC", blocked) != null && DesktopSafety.BlockedAppRefusal("1password", blocked) != null
                && DesktopSafety.BlockedAppRefusal("notepad", blocked) == null && DesktopSafety.BlockedAppRefusal("KeePass", null) == null);
            Check("DK3b", "password managers are blocked by default", new AppSettings().DesktopBlockedApps.Contains("Bitwarden"));
            await Task.CompletedTask;
        });
    }

    const string ToolCallNormalizerKey = CluadeX.Helpers.ToolCallNormalizer.InvalidArgumentsKey;
}
