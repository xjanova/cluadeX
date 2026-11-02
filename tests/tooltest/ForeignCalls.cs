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
        await Case("DK4", async () =>
        {
            // Terminals and run boxes run what is typed into them: the desktop tools only look (no keys, typing, clicks).
            Check("DK4", "terminals are command surfaces: by process (cmd, pwsh, Windows Terminal, mintty) and by window class",
                DesktopSafety.IsCommandSurface("cmd", "", "C:\\WINDOWS\\system32\\cmd.exe") && DesktopSafety.IsCommandSurface("pwsh", "", "x")
                && DesktopSafety.IsCommandSurface("WindowsTerminal", "", "Windows PowerShell") && DesktopSafety.IsCommandSurface("mintty", "", "MINGW64")
                && DesktopSafety.IsCommandSurface("someapp", "ConsoleWindowClass", "x") && DesktopSafety.IsCommandSurface("x", "CASCADIA_HOSTING_WINDOW_CLASS", "x"));
            Check("DK4b", "the Run box and Task Manager's Create new task are command surfaces; a normal explorer dialog is not",
                DesktopSafety.IsCommandSurface("explorer", "#32770", "Run") && DesktopSafety.IsCommandSurface("explorer", "#32770", "เรียกใช้")
                && DesktopSafety.IsCommandSurface("Taskmgr", "#32770", "Create new task")
                && !DesktopSafety.IsCommandSurface("explorer", "#32770", "Confirm File Delete") && !DesktopSafety.IsCommandSurface("explorer", "CabinetWClass", "Run"));
            Check("DK4c", "ordinary apps are not command surfaces (notepad, VS Code's window, a browser)",
                !DesktopSafety.IsCommandSurface("notepad", "Notepad", "Untitled") && !DesktopSafety.IsCommandSurface("Code", "Chrome_WidgetWin_1", "Program.cs")
                && !DesktopSafety.IsCommandSurface("chrome", "Chrome_WidgetWin_1", "Run — Google Search"));
            var term = new DesktopWindow(IntPtr.Zero, "Windows PowerShell", "CASCADIA_HOSTING_WINDOW_CLASS", 1, "WindowsTerminal", 0, 0, 800, 600, true, false);
            var pad = new DesktopWindow(IntPtr.Zero, "notes.txt", "Notepad", 2, "notepad", 0, 0, 800, 600, true, false);
            string? why = DesktopSafety.CommandSurfaceRefusal(term);
            Check("DK4d", "the refusal names the window and points to run_command; a normal window gets none",
                why != null && why.Contains("WindowsTerminal") && why.Contains("run_command") && DesktopSafety.CommandSurfaceRefusal(pad) == null
                && !DesktopSafety.IsEmbeddedTerminal(null), why ?? "");
            await Task.CompletedTask;
        });
        // ── the command-surface holes found in review (2026-11-02): pure rules only, no input is sent ──
        await Case("DK5", async () =>
        {
            Check("DK5", "Explorer's / a file dialog's address bar is a command box: Address Band Root, 41477, PART_AutoSuggestBox",
                DesktopSafety.IsAddressBarPart("Address Band Root", "") && DesktopSafety.IsAddressBarPart("Edit", "41477")
                && DesktopSafety.IsAddressBarPart("TextBox", "PART_AutoSuggestBox") && DesktopSafety.IsAddressBarPart("Breadcrumb Parent", ""));
            Check("DK5b", "…the file list, the tree and the file-name box are not",
                !DesktopSafety.IsAddressBarPart("DirectUIHWND", "") && !DesktopSafety.IsAddressBarPart("SysTreeView32", "100")
                && !DesktopSafety.IsAddressBarPart("Edit", "1148") && !DesktopSafety.IsAddressBarPart(null, null));
            Check("DK5c", "the bare ids 41477 / PART_AutoSuggestBox count only in explorer; the address band's classes in any process (file dialogs)",
                !DesktopSafety.IsAddressBarPart("TextBox", "PART_AutoSuggestBox", "MyWpfApp") && !DesktopSafety.IsAddressBarPart("Edit", "41477", "notepad")
                && DesktopSafety.IsAddressBarPart("Edit", "41477", "explorer") && DesktopSafety.IsAddressBarPart("Address Band Root", "", "notepad"));
            await Task.CompletedTask;
        });
        await Case("DK6", async () =>
        {
            Check("DK6", "an Enter or double-click on a program or script in Explorer is refused: .bat .cmd .ps1 .exe .lnk .vbs .hta",
                new[] { "build.bat", "x.CMD", "deploy.ps1", "setup.exe", "Tool.lnk", "a.vbs", "app.hta", " run.py " }.All(DesktopSafety.IsRunnableFileName));
            Check("DK6b", "…documents, folders and odd names open as usual",
                new[] { "notes.txt", "report.docx", "photo.png", "src", "", "a<b.bat" }.All(n => !DesktopSafety.IsRunnableFileName(n)));
            Check("DK6c", "with extensions hidden the item's Type decides: batch file, application, shortcut, script are programs",
                new[] { "Windows Batch File", "Application", "Shortcut", "Internet Shortcut", "Windows PowerShell Script", "VBScript Script File", "Registration Entries" }
                    .All(t => DesktopSafety.IsRunnableItemType(t) == true));
            Check("DK6d", "…folders (even one named chart.js), zip folders and documents are not; no type is unknown",
                new[] { "File folder", "Compressed (zipped) Folder", "Text Document", "PNG File", "Local Disk" }.All(t => DesktopSafety.IsRunnableItemType(t) == false)
                && DesktopSafety.IsRunnableItemType("") == null && DesktopSafety.IsRunnableItemType(null) == null);
            Check("DK6e", "context-menu commands that open the selection: Open, Run as administrator, Open with, Thai; Copy / Properties are not",
                new[] { "Open", "Run as administrator", "Open with", "Open with...", "&Open", "เปิด", "เรียกใช้ในฐานะผู้ดูแลระบบ", "Merge" }.All(DesktopSafety.IsOpenCommand)
                && new[] { "Copy", "Properties", "Open file location", "Open in new window", "Rename", "" }.All(n => !DesktopSafety.IsOpenCommand(n)));
            await Task.CompletedTask;
        });
        await Case("DK7", async () =>
        {
            var p = DesktopToolModule.TypingPieces("Terminal: Create New Terminal\r\nnpm test\n");
            Check("DK7", "desktop_type goes out line by line: every Enter its own piece, so the focus is checked again after it",
                p.SequenceEqual(new[] { "Terminal: Create New Terminal", "\n", "npm test", "\n" }), string.Join("|", p));
            var long1 = DesktopToolModule.TypingPieces(new string('a', 70), 32);
            string emoji = new string('x', 31) + "😀" + "y";
            var pair = DesktopToolModule.TypingPieces(emoji, 32);
            Check("DK7b", "…long lines in chunks (checked between them), never splitting a surrogate pair, nothing lost",
                long1.Count == 3 && long1.All(s => s.Length <= 32) && string.Concat(long1).Length == 70
                && string.Concat(pair) == emoji && pair.All(s => !char.IsHighSurrogate(s[^1])), string.Join("|", pair));
            Check("DK7c", "empty text is no pieces", DesktopToolModule.TypingPieces("").Count == 0 && DesktopToolModule.TypingPieces(null).Count == 0);
            var tabbed = DesktopToolModule.TypingPieces("name\tvalue");
            Check("DK7d", "…and a Tab is its own piece too (it can move the focus)", tabbed.SequenceEqual(new[] { "name", "\t", "value" }), string.Join("|", tabbed));
            await Task.CompletedTask;
        });
        await Case("DK8", async () =>
        {
            // Pure: the text is rebuilt with ToUnicodeEx in a given layout (the keyboard state is left untouched) — no key
            // is sent. US English, when this PC has it loaded, so the expected characters are fixed.
            IntPtr us = DesktopInput.LoadedLayouts().FirstOrDefault(h => (h.ToInt64() & 0xFFFFFFFF) == 0x04090409);
            if (us == IntPtr.Zero)
            {
                GapIf("DK8", "desktop_keys' typed text is rebuilt in the window's keyboard layout", false, "no US English layout is loaded on this PC");
                return;
            }
            string K(string keys, int repeat = 1, bool caps = false) => DesktopToolModule.KeysTypedText(keys, repeat, us, caps, out _);
            string Why(string keys) { DesktopToolModule.KeysTypedText(keys, 1, us, false, out var why); return why; }
            // An inert sentinel, never a real command (see desktoptest L1g).
            string t = K("e c h o space C X shift+a Enter");
            Check("DK8", "desktop_keys' typed text is what the keys really type: a bare letter is lower case, shift+a is A, space, Enter",
                t == "echo cxA\n" && Why("e c h o space C X shift+a Enter") == null, t.Replace("\n", "\\n"));
            Check("DK8b", "…repeat multiplies it, ctrl chords and F-keys type nothing, shift++ is a plus",
                K("a b", 3) == "ababab" && K("ctrl+s F5 Tab") == "\t" && K("shift++") == "+" && K("ctrl+v") == "" && K(null) == "");
            Check("DK8c", "shifted symbols are the shifted character: shift+; is ':', shift+7 is '&', shift+\\ is '|'",
                K("shift+;") == ":" && K("shift+7") == "&" && K("shift+\\") == "|" && K("C shift+; \\") == "c:\\",
                $"{K("shift+;")} {K("shift+7")} {K("shift+\\")} {K("C shift+; \\")}");
            Check("DK8d", "the plus key types what VK_OEM_PLUS types ('='), a typed '+' is shift+=",
                K("plus") == "=" && K("+") == "" && K("shift+plus") == "+", $"{K("plus")} {K("shift+plus")}");
            Check("DK8e", "Backspace takes back the last character (C : x Backspace \\ types c:\\)",
                K("C shift+; x Backspace \\") == "c:\\" && K("a Enter Backspace b") == "a\nb", K("C shift+; x Backspace \\"));
            Check("DK8f", "Caps Lock: on, a bare letter is upper case; the capslock key in the sequence flips it",
                K("a", caps: true) == "A" && K("capslock a") == "A" && K("shift+a", caps: true) == "a");
            Check("DK8g", "typed characters mixed with keys that move the caret or edit (Left, Home, Delete, Esc, alt+d) are refused: the text cannot be known",
                Why("C shift+; Left r d") != null && Why("x Home y") != null && Why("a b Delete") != null && Why("x x Esc r d") != null
                && Why("alt+d e c h o") != null && Why("C Left") is { } msg && msg.Contains("desktop_type"), Why("C Left") ?? "(none)");
            Check("DK8h", "…keys without typed text pass: Tab Tab Down Enter, End shift+Home Delete, space to toggle with arrows",
                Why("Tab Tab Down Enter") == null && Why("End shift+Home Delete") == null && Why("Tab space Down space") == null && Why("ctrl+a ctrl+c") == null);
            await Task.CompletedTask;
        });
        await Case("DK9", async () =>
        {
            Check("DK9", "xterm.js parts are terminals by class, whichever part is under the mouse",
                DesktopSafety.IsXtermPart("terminal xterm") && DesktopSafety.IsXtermPart("xterm-screen") && DesktopSafety.IsXtermPart("xterm-helper-textarea")
                && !DesktopSafety.IsXtermPart("monaco-editor") && !DesktopSafety.IsXtermPart(null));
            Check("DK9b", "Visual Studio's and JetBrains' terminal panes are terminals; the same name in another app is not",
                DesktopSafety.IsIdeTerminalPart("devenv", "Developer PowerShell", "") && DesktopSafety.IsIdeTerminalPart("pycharm64", "Terminal", "")
                && DesktopSafety.IsIdeTerminalPart("devenv", "Package Manager Console", "") && DesktopSafety.IsIdeTerminalPart("rider64", "", "TerminalToolWindow")
                && !DesktopSafety.IsIdeTerminalPart("notepad", "Terminal", "") && !DesktopSafety.IsIdeTerminalPart("devenv", "Solution Explorer", ""));
            Check("DK9c", "…named for the tool window ('Terminal: Local', 'Terminal - pwsh', 'Developer PowerShell for VS 2022')",
                DesktopSafety.IsIdeTerminalPart("idea64", "Terminal: Local", "") && DesktopSafety.IsIdeTerminalPart("rider64", "Terminal - pwsh", "")
                && DesktopSafety.IsIdeTerminalPart("devenv", "Developer PowerShell for VS 2022", "") && DesktopSafety.IsIdeTerminalPart("idea64", "", "JediTermWidget"));
            Check("DK9d", "…never a document, editor tab or tree item named after a file or folder",
                !DesktopSafety.IsIdeTerminalPart("devenv", "TerminalHelper.cs", "") && !DesktopSafety.IsIdeTerminalPart("rider64", "Editor for TerminalView.kt", "")
                && !DesktopSafety.IsIdeTerminalPart("pycharm64", "terminal.py", "") && !DesktopSafety.IsIdeTerminalPart("devenv", "terminal", "", "ControlType.TreeItem")
                && !DesktopSafety.IsIdeTerminalPart("devenv", "Terminal", "", "ControlType.TabItem") && !DesktopSafety.IsIdeTerminalPart("idea64", "terminal_utils.ts", "")
                && !DesktopSafety.IsIdeTerminalPart("devenv", "", "TerminalHelper"));
            await Task.CompletedTask;
        });
        await Case("DK10", async () =>
        {
            Check("DK10", "the Run box is known by structure in any language: a #32770 whose control 12298 is a combo box",
                DesktopSafety.LooksLikeRunBox("#32770", "ComboBox") && !DesktopSafety.LooksLikeRunBox("#32770", "Button")
                && !DesktopSafety.LooksLikeRunBox("#32770", null) && !DesktopSafety.LooksLikeRunBox("Notepad", "ComboBox"));
            Check("DK10b", "launching the Run box's shell folder or shell32's RunFileDlg is refused; ordinary launches are not",
                DesktopSafety.RunBoxLaunchRefusal("explorer.exe shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}") != null
                && DesktopSafety.RunBoxLaunchRefusal("rundll32.exe shell32.dll,#61") != null && DesktopSafety.RunBoxLaunchRefusal("rundll32 SHELL32, #61") != null
                && DesktopSafety.RunBoxLaunchRefusal("explorer.exe C:\\src") == null && DesktopSafety.RunBoxLaunchRefusal("notepad.exe a.txt") == null);
            await Task.CompletedTask;
        });
        await Case("DK11", async () =>
        {
            Check("DK11", "a shell, interpreter, script or explorer with arguments is approved per command line",
                DesktopSafety.LaunchesCommands("powershell.exe", "-c x") && DesktopSafety.LaunchesCommands("C:\\Windows\\System32\\cmd.exe", "")
                && DesktopSafety.LaunchesCommands("\"python\"", "a.py") && DesktopSafety.LaunchesCommands("tools\\go.bat", "")
                && DesktopSafety.LaunchesCommands("mshta", "x") && DesktopSafety.LaunchesCommands("explorer.exe", "C:\\src"));
            Check("DK11b", "…a GUI app started with no arguments (or explorer with nothing) keeps the per-app approval",
                !DesktopSafety.LaunchesCommands("notepad.exe", "") && !DesktopSafety.LaunchesCommands("C:\\Games\\Godot.exe", "")
                && !DesktopSafety.LaunchesCommands("explorer", "") && !DesktopSafety.LaunchesCommands("", ""));
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Check("DK11e", "any arguments make a command line (reg, notepad a.txt, git-bash -c, a Store app's AUMID); a console program does with none",
                DesktopSafety.LaunchesCommands("reg.exe", "query HKCU") && DesktopSafety.LaunchesCommands("notepad.exe", "a.txt")
                && DesktopSafety.LaunchesCommands("git-bash.exe", "-c x") && DesktopSafety.LaunchesCommands("shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "-d C:\\")
                && DesktopSafety.LaunchesCommands(Path.Combine(sys32, "reg.exe"), "") && DesktopSafety.LaunchesCommands("sc", ""));
            Check("DK11f", "…the PE header tells console from GUI programs",
                DesktopSafety.IsConsoleProgram(Path.Combine(sys32, "reg.exe")) && DesktopSafety.IsConsoleProgram(Path.Combine(sys32, "cmd.exe"))
                && !DesktopSafety.IsConsoleProgram(Path.Combine(sys32, "notepad.exe")) && !DesktopSafety.IsConsoleProgram(Path.Combine(sys32, "no-such-program.exe")));
            var module = new DesktopToolModule(null, null, null, _ => null);
            var shell = module.DescribePermission(new ToolCall { Type = ToolType.DesktopLaunch, ToolName = "desktop_launch",
                Arguments = new() { ["path"] = "powershell.exe", ["args"] = "-NoProfile -c Get-Date" } }, new ToolModuleContext());
            var other = module.DescribePermission(new ToolCall { Type = ToolType.DesktopLaunch, ToolName = "desktop_launch",
                Arguments = new() { ["path"] = "powershell.exe", ["args"] = "-NoProfile -c Get-Location" } }, new ToolModuleContext());
            var app = module.DescribePermission(new ToolCall { Type = ToolType.DesktopLaunch, ToolName = "desktop_launch",
                Arguments = new() { ["path"] = "notepad.exe" } }, new ToolModuleContext());
            var reg1 = module.DescribePermission(new ToolCall { Type = ToolType.DesktopLaunch, ToolName = "desktop_launch",
                Arguments = new() { ["path"] = "reg.exe", ["args"] = "query HKCU\\Software" } }, new ToolModuleContext());
            var reg2 = module.DescribePermission(new ToolCall { Type = ToolType.DesktopLaunch, ToolName = "desktop_launch",
                Arguments = new() { ["path"] = "reg.exe", ["args"] = "delete HKCU\\Software\\x /f" } }, new ToolModuleContext());
            Check("DK11c", "desktop_launch powershell: scope execute, the command line as resource, its own once-key, no \"Allow in this chat\"",
                shell.Scope == "execute" && shell.Resource == "powershell.exe -NoProfile -c Get-Date" && shell.ChatScopeKey == null
                && shell.OnceKey != null && !shell.OnceKey.StartsWith("desktop:") && shell.OnceKey != other.OnceKey && shell.AskEvenWhenPermissionsOff,
                $"{shell.Scope} | {shell.Resource} | {shell.OnceKey} | {shell.ChatScopeKey}");
            Check("DK11d", "…notepad keeps the per-app desktop approval", app.Scope == "desktop" && app.Resource == "notepad" && app.ChatScopeKey == "desktop-chat:notepad",
                $"{app.Scope} | {app.Resource} | {app.ChatScopeKey}");
            Check("DK11g", "approving `reg query` does not cover `reg delete`: each command line has its own key, none for the chat",
                reg1.Scope == "execute" && reg2.Scope == "execute" && reg1.OnceKey != reg2.OnceKey && reg1.ChatScopeKey == null && reg2.ChatScopeKey == null,
                $"{reg1.OnceKey} | {reg2.OnceKey}");
            await Task.CompletedTask;
        });
        await Case("DK12", async () =>
        {
            Check("DK12", "a Run box can be dismissed: Cancel (id 2), Close, Esc / alt+F4 alone",
                DesktopSafety.IsDismissControl("2", "") && DesktopSafety.IsDismissControl("Close", "") && DesktopSafety.IsDismissControl("", "Cancel")
                && DesktopSafety.IsDismissKeys("Esc") && DesktopSafety.IsDismissKeys("alt+F4") && DesktopSafety.IsDismissKeys("escape esc"));
            Check("DK12b", "…OK, Browse and any other keys are not dismissing",
                !DesktopSafety.IsDismissControl("1", "OK") && !DesktopSafety.IsDismissControl("12288", "Browse...")
                && !DesktopSafety.IsDismissKeys("Esc Enter") && !DesktopSafety.IsDismissKeys("") && !DesktopSafety.IsDismissKeys("a"));
            var runBox = new DesktopWindow(IntPtr.Zero, "Run", "#32770", 3, "explorer", 0, 0, 400, 200, true, false);
            var term = new DesktopWindow(IntPtr.Zero, "cmd", "ConsoleWindowClass", 4, "cmd", 0, 0, 800, 600, true, false);
            Check("DK12c", "dismissing lifts the refusal for a Run box only, never a terminal",
                DesktopSafety.CommandSurfaceRefusal(runBox) != null && DesktopSafety.CommandSurfaceRefusal(runBox, null, dismissing: true) == null
                && DesktopSafety.CommandSurfaceRefusal(term, null, dismissing: true) != null
                && DesktopSafety.IsTerminalWindow("cmd", "") && !DesktopSafety.IsTerminalWindow("explorer", "#32770"));
            Check("DK12d", "Esc, Tab and F6 may leave an address bar; Enter, arrows and text may not",
                DesktopSafety.IsLeaveChord("Esc") && DesktopSafety.IsLeaveChord("shift+Tab") && DesktopSafety.IsLeaveChord("F6")
                && !DesktopSafety.IsLeaveChord("Enter") && !DesktopSafety.IsLeaveChord("Down") && !DesktopSafety.IsLeaveChord("a") && !DesktopSafety.IsLeaveChord("ctrl+v"));
            await Task.CompletedTask;
        });
        await Case("DK13", async () =>
        {
            Check("DK13", "palette commands that send text to the editor's terminal (or run code there) are known by their label",
                new[] { "Terminal: Run Selected Text In Active Terminal", "Terminal: Run Active File In Active Terminal", "Terminal: Run Recent Command...",
                        "Python: Run Selection/Line in Python Terminal", "Python: Run Python File in Terminal", "Tasks: Run Build Task", "Run Code",
                        "Terminal: Send Custom Sequence to Terminal", "workbench.action.terminal.sendSequence", "Jupyter: Run Current File in Interactive Window" }
                    .All(DesktopSafety.NamesTerminalSend));
            Check("DK13b", "…ordinary commands are not, nor opening a terminal (typing into one is refused on its own)",
                new[] { "Format Document", "Terminal: Create New Terminal", "Open in Integrated Terminal", "Debug: Toggle Breakpoint", "View: Toggle Terminal",
                        "Run and Debug (Ctrl+Shift+D)", "View: Show Run and Debug", "Debug Console", "Run", "" }
                    .All(l => !DesktopSafety.NamesTerminalSend(l)));
            Check("DK13c", "what a model types into the palette to reach them counts too: prefixes and run-together words",
                new[] { "run sel", ">run act", "runselectedtext", "selected text active terminal", "run selection/line", "Run Python", "sendSeq" }
                    .All(DesktopSafety.TypedNamesTerminalSend));
            Check("DK13d", "…while ordinary text does not, and it is judged only in an editor's palette (prose in Word is never refused)",
                !DesktopSafety.TypedNamesTerminalSend("hello world") && !DesktopSafety.TypedNamesTerminalSend("format doc")
                && DesktopSafety.TerminalSendRefusal("run the selected file", null) == null
                && DesktopSafety.IsTerminalHost("Code") && DesktopSafety.IsTerminalHost("rider64") && !DesktopSafety.IsTerminalHost("WINWORD"));
            await Task.CompletedTask;
        });
        // ── round 3 of the review (2026-11-02): pure rules only, no input is sent ──
        await Case("DK14", async () =>
        {
            // With extensions hidden setup.py and setup.cfg both show as "setup": the selected setup.cfg says nothing about
            // the other "setup" row a double-click is about to select and open.
            var cfg = new[] { (@"C:\repo\setup.cfg", false) };
            Check("DK14", "a 'setup' row that is not selected is not judged by a selected setup.cfg; the selected row itself is",
                DesktopSafety.SelectedItemRuns("setup", false, cfg) == null && DesktopSafety.SelectedItemRuns("setup", true, cfg) == false);
            var both = new[] { (@"C:\repo\setup.cfg", false), (@"C:\repo\setup.py", false) };
            var docs = new[] { (@"C:\repo\setup.cfg", false), (@"C:\repo\setup.txt", false) };
            Check("DK14b", "several selected files under one name: a program among them counts, only documents leave it to the disk",
                DesktopSafety.SelectedItemRuns("setup", true, both) == true && DesktopSafety.SelectedItemRuns("setup", true, docs) == null
                && DesktopSafety.SelectedItemRuns("deploy", true, new[] { (@"C:\repo\deploy.bat", false) }) == true);
            string dir = P("Foreign", "shown");
            Directory.CreateDirectory(Path.Combine(dir, "lib"));
            foreach (var f in new[] { "setup.cfg", "setup.py", "notes.txt" }) File.WriteAllText(Path.Combine(dir, f), "x");
            var folders = new[] { dir };
            Check("DK14c", "the disk decides then: 'setup' beside setup.py is a script; notes (notes.txt) and a folder are not; no file is unknown",
                DesktopSafety.DiskItemRuns("setup", folders) == true && DesktopSafety.DiskItemRuns("notes", folders) == false
                && DesktopSafety.DiskItemRuns("lib", folders) == false && DesktopSafety.DiskItemRuns("nothing", folders) == null
                && DesktopSafety.DiskItemRuns("a<b", folders) == null);
            await Task.CompletedTask;
        });
        await Case("DK15", async () =>
        {
            Check("DK15", "Explorer menu commands known to leave the selection unopened pass: Copy, Cut, Rename, Properties, Show more options, Send to, Thai",
                new[] { "Copy", "Cu&t", "Copy\tCtrl+C", "Copy (Ctrl+C)", "Rename(&M)", "Properties", "Show more options", "Open file location",
                        "Copy as path", "Send to", "Delete", "Edit", "New", "Sort by", "คัดลอก", "เปลี่ยนชื่อ", "คุณสมบัติ", "แสดงตัวเลือกเพิ่มเติม" }
                    .All(DesktopSafety.IsHarmlessMenuCommand));
            Check("DK15b", "…everything else counts as opening: Run with PowerShell, Open with Command Prompt, Repair, Uninstall, the apps under Open with, Send to's entries",
                new[] { "Run with PowerShell", "เรียกใช้ด้วย PowerShell", "Open with Command Prompt", "Repair", "Uninstall", "Open", "Open with",
                        "Run as administrator", "Windows PowerShell", "Python", "Desktop (create shortcut)", "Compressed (zipped) folder",
                        "Mail recipient", "Copy to folder and run", "" }
                    .All(n => !DesktopSafety.IsHarmlessMenuCommand(n)));
            await Task.CompletedTask;
        });
        await Case("DK16", async () =>
        {
            DesktopChord K(string s) => DesktopInput.Parse(s) ?? throw new Exception("unparsed chord " + s);
            string? R(string proc, string keys, bool inEditor = false) => DesktopSafety.RunChordRefusal(proc, K(keys), () => inEditor);
            Check("DK16", "VS Code (and Visual Studio): ctrl+shift+B, F5, ctrl+F5 run a task, build or launch configuration wherever the focus is",
                R("Code", "ctrl+shift+B") != null && R("Code", "F5") != null && R("Cursor", "ctrl+F5") != null && R("Code", "ctrl+shift+F5") != null
                && R("devenv", "ctrl+shift+b") != null && R("devenv", "F5") is { } why && why.Contains("run_command"), R("devenv", "F5") ?? "(none)");
            Check("DK16b", "JetBrains: shift+F10, ctrl+shift+F10, shift+F9 and ctrl+F5 run a configuration",
                R("idea64", "shift+F10") != null && R("pycharm64", "ctrl+shift+F10") != null && R("rider64", "shift+F9") != null && R("webstorm64", "ctrl+F5") != null);
            Check("DK16c", "…shift+F5 (stop), shift+F10 in VS Code, F10, ctrl+B and these keys in other apps pass; the run-line keys still need an editor",
                R("Code", "shift+F5") == null && R("Code", "shift+F10") == null && R("Code", "F10") == null && R("Code", "ctrl+b") == null
                && R("notepad", "F5") == null && R("chrome", "ctrl+shift+B") == null && R("explorer", "F5") == null && R("idea64", "F10") == null
                && R("Code", "shift+Enter") == null && R("Code", "shift+Enter", inEditor: true) != null && R("pycharm64", "alt+shift+E") != null);
            Check("DK16d", "the 'task ' quick-access prefix (and '>task') selects tasks: typed into the palette it is refused, and Enter in the task picker",
                DesktopSafety.TypedNamesTerminalSend("task deploy") && DesktopSafety.TypedNamesTerminalSend(">task") && DesktopSafety.TypedNamesTerminalSend("> Tasks: Configure")
                && !DesktopSafety.TypedNamesTerminalSend("taskfile.yml") && !DesktopSafety.TypedNamesTerminalSend("my task list")
                && DesktopSafety.IsTaskPicker("task deploy", "") && DesktopSafety.IsTaskPicker("", "Select the task to run")
                && !DesktopSafety.IsTaskPicker("deploy", "Search files by name") && !DesktopSafety.IsTaskPicker(null, null));
            await Task.CompletedTask;
        });
        await Case("DK17", async () =>
        {
            DesktopChord K(string s) => DesktopInput.Parse(s) ?? throw new Exception("unparsed chord " + s);
            Check("DK17", "in an Explorer context menu only an access key can choose a command by itself: o, alt+o, a paste",
                DesktopSafety.IsMenuAccessKey(K("o")) && DesktopSafety.IsMenuAccessKey(K("alt+o")) && DesktopSafety.IsMenuAccessKey(K("ctrl+v")));
            Check("DK17b", "…Esc, the arrows, Tab and alt+F4 only move or close it (never refused for the selection); Enter and space go by the focused command",
                new[] { "Esc", "Up", "Down", "Left", "Right", "Tab", "shift+Tab", "alt+F4", "Enter", "space", "Home" }.All(k => !DesktopSafety.IsMenuAccessKey(K(k))));
            Check("DK17c", "ctrl+Esc opens Start like the Windows key; Esc and ctrl+shift+Esc do not",
                DesktopSafety.OpensStart(K("ctrl+Esc")) && !DesktopSafety.OpensStart(K("Esc")) && !DesktopSafety.OpensStart(K("ctrl+shift+Esc")));
            await Task.CompletedTask;
        });
        await Case("DK18", async () =>
        {
            // The shell's Auto-Suggest Dropdown also opens under a file dialog's file-name box: only an address bar's counts.
            var nameBox = new (string?, string?)[] { ("Edit", "1001"), ("ComboBox", "1148"), ("ComboBoxEx32", "1148"), ("DUIViewWndClassName", ""), ("#32770", "") };
            var address = new (string?, string?)[] { ("Edit", "41477"), ("ComboBox", ""), ("ComboBoxEx32", ""), ("Address Band Root", ""), ("#32770", "") };
            var popup = new (string?, string?)[] { ("ListBox", ""), ("Auto-Suggest Dropdown", "") };
            Check("DK18", "a suggestion under a Save dialog's file-name box is not the address bar's; under its address band (or the popup itself) it is",
                !DesktopSafety.IsAddressBarFocusPath(nameBox, "notepad") && DesktopSafety.IsAddressBarFocusPath(address, "notepad")
                && DesktopSafety.IsAddressBarFocusPath(popup, "notepad") && !DesktopSafety.IsAddressBarFocusPath(Array.Empty<(string?, string?)>(), "notepad"));
            await Task.CompletedTask;
        });
        await Case("DK19", async () =>
        {
            var search = new DesktopWindow(IntPtr.Zero, "Search", "Windows.UI.Core.CoreWindow", 5, "SearchHost", 0, 0, 800, 600, true, false);
            var tray = new DesktopWindow(IntPtr.Zero, "", "Shell_TrayWnd", 6, "explorer", 0, 0, 1920, 48, true, false);
            Check("DK19", "Windows Search / Start are command surfaces: SearchHost, StartMenuExperienceHost, SearchApp (Windows 10); the taskbar is not",
                DesktopSafety.IsCommandSurface("SearchHost", "Windows.UI.Core.CoreWindow", "Search") && DesktopSafety.IsCommandSurface("StartMenuExperienceHost", "", "Start")
                && DesktopSafety.IsCommandSurface("SearchApp", "", "Search") && DesktopSafety.IsCommandSurface(search) && !DesktopSafety.IsCommandSurface(tray)
                && !DesktopSafety.IsRunBox(search) && !DesktopSafety.IsTerminalWindow("SearchHost", ""));
            string? why = DesktopSafety.CommandSurfaceRefusal(search);
            Check("DK19b", "typing, Enter and clicks there are refused (pointing to desktop_launch); Esc / close (dismissing) is not",
                why != null && why.Contains("desktop_launch") && DesktopSafety.CommandSurfaceRefusal(search, null, dismissing: true) == null
                && DesktopSafety.CommandSurfaceRefusal(tray) == null, why ?? "(none)");
            await Task.CompletedTask;
        });
        // ── round 4 of the review (2026-11-03): pure rules only, no input is sent ──
        await Case("DK20", async () =>
        {
            // One rule for Enter, a click and an invoke on a palette row: in the task picker every row runs a task, whatever it is called.
            Check("DK20", "a row of VS Code's task picker runs a task however it is reached ('task ' typed, or the picker's placeholder), whatever the row is called",
                DesktopSafety.PaletteRunRefusal("Code", "task ", "", "deploy, Workspace") is { } t && t.Contains("tasks.json")
                && DesktopSafety.PaletteRunRefusal("Cursor", "", "Select the task to run", "deploy") != null
                && DesktopSafety.PaletteRunRefusal("Code", "task deploy", "", null) != null,
                DesktopSafety.PaletteRunRefusal("Code", "task ", "", "deploy, Workspace") ?? "(none)");
            Check("DK20b", "…other rows go by their label (the row unknown: by the typed text); a file row of quick open runs nothing",
                DesktopSafety.PaletteRunRefusal("Code", ">run sel", "", "Terminal: Run Selected Text In Active Terminal") != null
                && DesktopSafety.PaletteRunRefusal("Code", ">run sel", "", null) != null
                && DesktopSafety.PaletteRunRefusal("Code", ">format", "", "Format Document") == null
                && DesktopSafety.PaletteRunRefusal("Code", ">format", "", null) == null
                && DesktopSafety.PaletteRunRefusal("Code", "deploy", "Search files by name", "deploy.ps1") == null);
            Check("DK20c", "'tas' then 'k ' passes piece by piece, so the palette's text is judged with what it already holds",
                !DesktopSafety.TypedNamesTerminalSend("tas", "Code") && !DesktopSafety.TypedNamesTerminalSend("k deploy", "Code")
                && DesktopSafety.TypedNamesTerminalSend("tas" + "k deploy", "Code"));
            await Task.CompletedTask;
        });
        await Case("DK21", async () =>
        {
            DesktopChord K(string s) => DesktopInput.Parse(s) ?? throw new Exception("unparsed chord " + s);
            string? R(string proc, string keys) => DesktopSafety.RunChordRefusal(proc, K(keys), () => false);
            Check("DK21", "starting a launch / run configuration by name is refused like F5: Start Debugging, Run Without Debugging, Run and Debug, JetBrains' Run 'app'",
                new[] { "Debug: Start Debugging", "Debug: Start Without Debugging", "Run Without Debugging", "Debug: Select and Start Debugging",
                        "Start Debugging", "Start Without Debugging", "Run and Debug", "Run 'Application'", "Debug 'app'", "Run...", "Debug…" }
                    .All(DesktopSafety.NamesTerminalSend));
            Check("DK21b", "…and typed toward: 'debug start', 'start debug', 'run without debug'; quick open's 'debug ' prefix is the launch picker",
                new[] { "debug start", ">debug: start", "start debug", "run without debug", "debug deploy" }.All(t => DesktopSafety.TypedNamesTerminalSend(t, "Code"))
                && !DesktopSafety.TypedNamesTerminalSend("debugger notes", "Code") && !DesktopSafety.TypedNamesTerminalSend(">debug", "Code")
                && DesktopSafety.IsLaunchPicker("debug deploy", "Code") && !DesktopSafety.IsLaunchPicker("debugger", "Code")
                && DesktopSafety.PaletteRunRefusal("Code", "debug ", "", "deploy") is { } l && l.Contains("launch.json"));
            Check("DK21c", "JetBrains: alt+shift+F10 / alt+shift+F9 (the Run / Debug popup) and ctrl+F9 (Build Project) are refused; alt+F9, F9 and VS Code's ctrl+F9 pass",
                R("idea64", "alt+shift+F10") != null && R("pycharm64", "alt+shift+F9") != null && R("idea64", "ctrl+F9") != null && R("studio64", "ctrl+F9") != null
                && R("idea64", "alt+F9") == null && R("idea64", "F9") == null && R("Code", "ctrl+F9") == null && R("Code", "alt+shift+F10") == null);
            await Task.CompletedTask;
        });
        await Case("DK22", async () =>
        {
            Check("DK22", "'task ' is VS Code's prefix only: 'task service' in JetBrains' Search Everywhere finds a class, the forks still refuse it",
                !DesktopSafety.TypedNamesTerminalSend("task service", "idea64") && !DesktopSafety.IsTaskPicker("task service", "", "idea64")
                && DesktopSafety.PaletteRunRefusal("idea64", "task service", "", "TaskService") == null
                && !DesktopSafety.IsLaunchPicker("debug x", "rider64")
                && DesktopSafety.TypedNamesTerminalSend("task deploy", "Cursor") && DesktopSafety.IsTaskPicker("task deploy", "", "Windsurf"));
            await Task.CompletedTask;
        });
        await Case("DK23", async () =>
        {
            var win11File = new[] { "Cut", "Copy", "Rename", "Share", "Delete", "Open", "Open with", "Compress to ZIP file", "Copy as path", "Properties", "Show more options" };
            var openWith = new[] { "Notepad", "Python", "Search the Microsoft Store", "Choose another app" };
            var sendTo = new[] { "Bluetooth device", "Compressed (zipped) folder", "Desktop (create shortcut)", "Documents", "Mail recipient" };
            var viewFlyout = new[] { "Extra large icons", "Large icons", "Medium icons", "Small icons", "List", "Details", "Tiles", "Content", "Compact view", "Show" };
            var sortFlyout = new[] { "Name", "Date modified", "Type", "Size", "Ascending", "Descending", "More options", "Group by" };
            var breadcrumb = new[] { "Desktop", "Documents", "repo", "tools" };
            var taskbar11 = new[] { "Task Manager", "Taskbar settings" };
            var seeMore = new[] { "Undo", "Redo", "Pin to Quick access", "Select all", "Select none", "Invert selection", "Copy path", "Properties", "Options" };
            Check("DK23", "a file's own menu (Windows 11's, the classic Thai one) and its Open with / Send to lists act on the selection",
                DesktopSafety.IsSelectionMenu(win11File, true) && DesktopSafety.IsSelectionMenu(new[] { "เปิด", "คัดลอก (Ctrl+C)", "คุณสมบัติ" }, true)
                && DesktopSafety.IsSelectionMenu(openWith, true) && DesktopSafety.IsSelectionMenu(sendTo, true));
            Check("DK23b", "…command-bar flyouts, breadcrumb dropdowns and the taskbar's menu do not; in an unknown display language (or unreadable) every menu does",
                !DesktopSafety.IsSelectionMenu(viewFlyout, true) && !DesktopSafety.IsSelectionMenu(sortFlyout, true)
                && !DesktopSafety.IsSelectionMenu(breadcrumb, true) && !DesktopSafety.IsSelectionMenu(taskbar11, true)
                && DesktopSafety.IsSelectionMenu(breadcrumb, false) && DesktopSafety.IsSelectionMenu(Array.Empty<string?>(), true));
            Check("DK23c", "the common verbs that open nothing pass: Compress to ZIP file, Create shortcut, Extract All, Pin / Unpin, Open in new tab, Undo, Options, the See more menu",
                new[] { "Compress to ZIP file", "Create shortcut", "Extract All...", "Pin to Quick access", "Unpin from Start", "Pin to taskbar", "Add to Favorites",
                        "Open in new tab", "Open in new window", "Restore previous versions", "Undo Rename", "Redo", "Options", "ZIP File", "Paste shortcut" }
                    .All(DesktopSafety.IsHarmlessMenuCommand)
                && seeMore.All(DesktopSafety.IsHarmlessMenuCommand)
                && !DesktopSafety.IsHarmlessMenuCommand("Troubleshoot compatibility") && !DesktopSafety.IsHarmlessMenuCommand("Notepad"));
            Check("DK23d", "View / Sort entries pass in their own submenu, never in Open with, Send to, a file's menu itself, or an unread menu",
                DesktopSafety.IsHarmlessMenuCommand("Details", viewFlyout) && DesktopSafety.IsHarmlessMenuCommand("Date modified", sortFlyout)
                && !DesktopSafety.IsHarmlessMenuCommand("Details", openWith.Append("Details")) && !DesktopSafety.IsHarmlessMenuCommand("Details", sendTo.Append("Details"))
                && !DesktopSafety.IsHarmlessMenuCommand("Details", win11File.Append("Details")) && !DesktopSafety.IsHarmlessMenuCommand("Details", Array.Empty<string?>())
                && !DesktopSafety.IsHarmlessMenuCommand("Details") && !DesktopSafety.IsHarmlessMenuCommand("Python", openWith));
            await Task.CompletedTask;
        });
        // ── round 5 of the review (2026-11-03): pure rules only, no input is sent ──
        await Case("DK24", async () =>
        {
            Check("DK24", "the other one-click ways to run the project's code are known by their label: Debug Python File, Debug using launch.json, Run / Debug Test, Run All Tests, npm's Run Script",
                new[] { "Python Debugger: Debug Python File", "Python: Debug Python File in Terminal", "Debug Current File", "Python Debugger: Debug using launch.json",
                        "Run Test", "Debug Test", "Run All Tests", "Test: Run All Tests", "Testing: Debug Tests in Current File", "run package tests", "debug test",
                        "Run Doctest", "run benchmark", "Rerun Failed Tests", "Test: Rerun Last Run", "npm: Run Script", "\u25B6\uFE0E Run Tests" }
                    .All(DesktopSafety.NamesTerminalSend));
            Check("DK24b", "…while Go to Test, the Debug Console, the test explorer, launch.json itself and a bare Run (the menu bar's) are not",
                new[] { "Go to Test", "Test: Go to Test", "Debug Console", "Debug: Focus on Debug Console View", "Testing: Focus on Test Explorer View",
                        "Debug: Open launch.json", "Debug: Toggle Breakpoint", "Tests", "Run", "Debug", "Run and Debug (Ctrl+Shift+D)", "Python: Select Interpreter" }
                    .All(l => !DesktopSafety.NamesTerminalSend(l)));
            Check("DK24c", "typed toward them counts too ('debug py', 'run test', 'debug test', 'run all tests'); 'go to test', 'test explorer', 'debugger notes' do not",
                new[] { "debug python file", "debugpy", ">python debugger: debug py", "debug using", "run test", "runtests", "debug test", "run all tests" }
                    .All(t => DesktopSafety.TypedNamesTerminalSend(t, "Code"))
                && new[] { "go to test", "console", "test explorer", "debugger notes", "testing" }.All(t => !DesktopSafety.TypedNamesTerminalSend(t, "Code")));
            Check("DK24d", "a bare 'Run' / 'Debug' button, link or CodeLens ('\u25B6\uFE0E Run') runs code in VS Code; the menu bar's Run, other words and other apps do not",
                DesktopSafety.NamesViewRunButton("Code", "Run", inMenuBar: false) && DesktopSafety.NamesViewRunButton("Code", "Debug", inMenuBar: false)
                && DesktopSafety.NamesViewRunButton("Cursor", "\u25B6\uFE0E Run ", inMenuBar: false) && DesktopSafety.NamesViewRunButton("Code", "\u25B7 debug", inMenuBar: false)
                && !DesktopSafety.NamesViewRunButton("Code", "Run", inMenuBar: true) && !DesktopSafety.NamesViewRunButton("Code", "Run and Debug", inMenuBar: false)
                && !DesktopSafety.NamesViewRunButton("Code", "Runner", inMenuBar: false) && !DesktopSafety.NamesViewRunButton("Code", "Debug Console", inMenuBar: false)
                && !DesktopSafety.NamesViewRunButton("idea64", "Run", inMenuBar: false) && !DesktopSafety.NamesViewRunButton("explorer", "Run", inMenuBar: false));
            await Task.CompletedTask;
        });
        await Case("DK25", async () =>
        {
            DesktopChord K(string s) => DesktopInput.Parse(s) ?? throw new Exception("unparsed chord " + s);
            string? R(string proc, string keys) => DesktopSafety.RunChordRefusal(proc, K(keys), () => false);
            Check("DK25", "Rider (its Visual Studio keymap) is judged like Visual Studio: F5, ctrl+F5, ctrl+shift+F5 and ctrl+shift+B are refused",
                R("rider64", "F5") != null && R("rider64", "ctrl+shift+B") != null && R("rider", "ctrl+F5") != null && R("rider64", "ctrl+shift+F5") != null,
                R("rider64", "F5") ?? "(none)");
            Check("DK25b", "…not the other JetBrains IDEs (F5 is Copy there, ctrl+shift+B a navigation), nor Rider's shift+F5 (stop) or alt+F5",
                R("idea64", "F5") == null && R("pycharm64", "ctrl+shift+B") == null && R("idea64", "ctrl+shift+F5") == null && R("webstorm64", "F5") == null
                && R("rider64", "shift+F5") == null && R("rider64", "alt+F5") == null && R("rider64", "F6") == null);
            await Task.CompletedTask;
        });
        await Case("DK26", async () =>
        {
            var newFlyout = new[] { "Folder", "Shortcut", "Bitmap image", "Contact", "Microsoft Word Document", "Rich Text Format", "Text Document", "Compressed (zipped) Folder" };
            var sendToShort = new[] { "Bluetooth device", "Compressed (zipped) folder", "Documents" };
            Check("DK26", "the command bar's New flyout is not a file's menu although it holds a zip folder: New ▸ Folder / Text Document pass with a .bat selected",
                !DesktopSafety.IsSelectionMenu(newFlyout, true) && !DesktopSafety.IsSelectionMenu(new[] { "Folder", "Compressed (zipped) Folder" }, true)
                && !DesktopSafety.IsSelectionMenu(new[] { "โฟลเดอร์", "ทางลัด", "เอกสารข้อความ", "โฟลเดอร์ (บีบอัด)" }, true));
            Check("DK26b", "…Send to still is, by its zip folder beside Documents (no mail recipient or desktop shortcut needed), and in an unknown language every menu is",
                DesktopSafety.IsSelectionMenu(sendToShort, true) && DesktopSafety.IsSelectionMenu(new[] { "Compressed (zipped) folder", "Desktop (create shortcut)" }, true)
                && DesktopSafety.IsSelectionMenu(newFlyout, false) && !DesktopSafety.IsHarmlessMenuCommand("Compressed (zipped) folder"));
            Check("DK26c", "See more ▸ Copy path (and the Thai คัดลอกเส้นทาง) leave the selection unopened; 'Copy path and run' style names do not",
                DesktopSafety.IsHarmlessMenuCommand("Copy path") && DesktopSafety.IsHarmlessMenuCommand("คัดลอกเส้นทาง") && DesktopSafety.IsHarmlessMenuCommand("Copy path (Ctrl+Shift+C)")
                && !DesktopSafety.IsHarmlessMenuCommand("Copy path and run") && !DesktopSafety.IsHarmlessMenuCommand("Copy to folder and run"));
            await Task.CompletedTask;
        });
        await Case("DK27", async () =>
        {
            const string branch = "Please provide a new branch name (Press 'Enter' to confirm or 'Escape' to cancel)";
            const string quickOpen = "Search files by name (append : to go to line or @ to go to symbol)";
            Check("DK27", "a plain InputBox (Git: Create Branch, no list) runs no row: 'debug login crash', 'task fix', 'run tests later' pass there",
                !DesktopSafety.PalettePicks("Code", false, branch)
                && DesktopSafety.PaletteRunRefusal("Code", "debug login crash", branch, null, showsList: false) == null
                && DesktopSafety.PaletteRunRefusal("Code", "task fix", branch, null, showsList: false) == null
                && DesktopSafety.PaletteRunRefusal("Cursor", "run tests later", branch, null, showsList: false) == null);
            Check("DK27b", "…while a quick pick (a list shown), quick open by its placeholder, the task picker and JetBrains' palette keep every rule",
                DesktopSafety.PaletteRunRefusal("Code", "debug login crash", branch, null, showsList: true) is { } d && d.Contains("launch.json")
                && DesktopSafety.PalettePicks("Code", false, quickOpen) && DesktopSafety.PaletteRunRefusal("Code", "task deploy", quickOpen, null, showsList: false) != null
                && DesktopSafety.PaletteRunRefusal("Code", "", "Select the task to run", "deploy", showsList: true) != null
                && DesktopSafety.PaletteRunRefusal("Code", ">run sel", "", "Terminal: Run Selected Text In Active Terminal") != null
                && DesktopSafety.PalettePicks("idea64", false, "") && DesktopSafety.PaletteRunRefusal("rider64", "start debug", "", null, showsList: false) != null,
                DesktopSafety.PaletteRunRefusal("Code", "debug login crash", branch, null, showsList: true) ?? "(none)");
            await Task.CompletedTask;
        });
    }

    const string ToolCallNormalizerKey = CluadeX.Helpers.ToolCallNormalizer.InvalidArgumentsKey;
}
