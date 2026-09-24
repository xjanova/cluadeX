using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ToolTest;

/// <summary>
/// Black-box contract test for CluadeX's coding tools, driven through the REAL AgentToolService
/// and the same name→type resolver the native tool_use loop uses. Every call is phrased the way a
/// model actually phrases it (bash-isms, LF line endings, sloppy indentation), because the model
/// does not know it is on Windows and does not see the file's bytes.
///
/// Three outcomes per check:
///   PASS — does what a solid coding agent needs.
///   FAIL — broken: wrong result, corrupted file, crash, hang, or an error the model cannot act on.
///   GAP  — works, but weaker than the publicly documented Claude Code tool contract.
/// </summary>
internal static class Program
{
    static AgentToolService Svc;
    static FileSystemService Fs;
    static string Root;
    static int Pass, Fail, Gap;
    static long LastMs;
    static readonly StringBuilder Report = new();
    static readonly List<string> Problems = new();

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    // ───────────────────────── harness plumbing ─────────────────────────

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 420).Replace("\n", "\n            "));
    }

    static void GapIf(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Gap++; Problems.Add($"GAP  {id} {name}");
        Console.WriteLine($"  GAP  {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 420).Replace("\n", "\n            "));
    }

    static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");

    /// <summary>What the model actually reads back from a tool call.</summary>
    static string Seen(ToolResult r) =>
        r.Success ? (r.Output ?? "") :
        string.Join("\n", new[] { r.Error, r.Output }.Where(s => !string.IsNullOrEmpty(s)));

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall
        {
            Type = type,
            ToolName = tool,
            Arguments = args.ToDictionary(a => a.k, a => a.v),
        };
        var sw = Stopwatch.StartNew();
        ToolResult r;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            var task = Svc.ExecuteToolAsync(call, cts.Token);
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(95))) != task)
                r = new ToolResult { Success = false, Error = "HARNESS: tool call did not return within 95s (hang)" };
            else
                r = await task;
        }
        sw.Stop();
        LastMs = sw.ElapsedMilliseconds;
        Report.AppendLine($"### {tool}  {string.Join("  ", args.Select(a => $"{a.k}={Trunc(a.v.Replace("\r", "\\r").Replace("\n", "\\n"), 80)}"))}");
        Report.AppendLine($"success={r.Success}  {LastMs}ms  chars={Seen(r).Length}");
        Report.AppendLine("```");
        Report.AppendLine(Trunc(Seen(r), 2500));
        Report.AppendLine("```");
        return r;
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.Message, 200)}");
        }
    }

    static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    static string Read(string rel) => File.ReadAllText(P(rel.Split('/')));
    static byte[] Bytes(string rel) => File.ReadAllBytes(P(rel.Split('/')));
    static int LoneLf(string text) { int n = 0; for (int i = 0; i < text.Length; i++) if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) n++; return n; }
    static void WriteCrlf(string rel, string text) { var full = P(rel.Split('/')); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false)); }

    // ───────────────────────── fixture ─────────────────────────

    static void BuildFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "cluadex-tooltest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        WriteCrlf("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        WriteCrlf("App/Program.cs", """
            namespace App;

            public static class Program
            {
                public static void Main()
                {
                    var calc = new Calculator();
                    Console.WriteLine(calc.Add(2, 3));
                }
            }
            """);
        WriteCrlf("App/Calculator.cs", """
            namespace App;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }
            }
            """);
        WriteCrlf("App/Thai.cs", """
            namespace App;

            // ข้อความทดสอบภาษาไทย
            public static class Thai
            {
                public const string Greeting = "สวัสดีครับ";
            }
            """);
        // UTF-8 WITH BOM — Visual Studio's default for new C# files.
        File.WriteAllText(P("App", "Bom.cs"), "namespace App;\r\n\r\npublic static class Bom\r\n{\r\n    public const int Value = 1;\r\n}\r\n", new UTF8Encoding(true));
        WriteCrlf("dup.txt", "foo\nbar\nfoo\n");
        // Shebang script, no BOM — a BOM here turns "#!/bin/bash" into a "command not found".
        File.WriteAllText(P("run.sh"), "#!/bin/bash\necho hello\n", new UTF8Encoding(false));
        // Legacy Thai file in Windows-874 / TIS-620 ("สวัสดี" = CA C7 D1 CA B4 D5) — not UTF-8.
        File.WriteAllBytes(P("App", "Legacy.cs"), new byte[] { 0x2F, 0x2F, 0x20, 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5, 0x0D, 0x0A }
            .Concat(Encoding.ASCII.GetBytes("namespace App;\r\n\r\npublic class Legacy { }\r\n")).ToArray());
        // Minified bundle: one 200 KB line.
        Directory.CreateDirectory(P("docs"));
        File.WriteAllText(P("docs", "min.js"), "var a=1;" + new string('x', 200_000) + "\n");
        // Binary asset.
        Directory.CreateDirectory(P("assets"));
        File.WriteAllBytes(P("assets", "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52 });
        Directory.CreateDirectory(P("docs"));
        File.WriteAllText(P("docs", "big.txt"), string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i}")) + "\n");
        // Noise that search must ignore.
        WriteCrlf("App/bin/Debug/junk.cs", "class Calculator { } // build output copy");
        WriteCrlf("App/obj/junk.cs", "class Calculator { } // intermediate");
        WriteCrlf("ignored/secret.cs", "class Calculator { } // git-ignored");
        WriteCrlf(".gitignore", "bin/\nobj/\nignored/\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Root)!, "cluadex-outside.txt"), "outside the project");

        Git("init -q"); Git("config user.email t@t"); Git("config user.name t");
        Git("add -A"); Git("commit -q -m init");
    }

    static void Git(string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; p.WaitForExit(20000);
    }

    // ───────────────────────── run ─────────────────────────

    static async Task<int> Run()
    {
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        var provider = services.BuildServiceProvider();

        // SAFETY: refuse to run unless the data root is isolated next to this exe.
        var settings = provider.GetRequiredService<SettingsService>();
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }

        Fs = provider.GetRequiredService<FileSystemService>();
        Svc = provider.GetRequiredService<AgentToolService>();
        Svc.OnPermissionRequired += _ => Task.FromResult(true);   // the user clicks Allow

        BuildFixture();
        Fs.WorkingDirectory = Root;
        Console.WriteLine($"project   : {Root}\n");

        var schemas = Svc.BuildNativeToolSchemas().Where(s => s.Name is "multi_edit").ToList();
        foreach (var s in schemas)
            Report.AppendLine($"## schema {s.Name}\n```\n{System.Text.Json.JsonSerializer.Serialize(s.InputSchema)}\n```");

        Console.WriteLine("── read_file ──");
        await Case("R1", async () => { var r = await T("read_file", ("path", "App/Program.cs")); Check("R1", "reads a file", r.Success && Seen(r).Contains("calc.Add(2, 3)"), Seen(r)); });
        await Case("R1b", async () =>
        {
            var r = await T("read_file", ("path", "App/Program.cs"));
            var first = Seen(r).Split('\n').FirstOrDefault(l => l.Contains("namespace App")) ?? "";
            GapIf("R1b", "whole-file read shows line numbers (so errors like Program.cs(8,9) can be located)", first.TrimStart().StartsWith("1"), "first line looks like: " + first);
        });
        await Case("R2", async () => { var r = await T("read_file", ("path", "App/Missing.cs")); Check("R2", "missing file → clear error naming the file", !r.Success && Seen(r).Contains("Missing.cs"), Seen(r)); });
        await Case("R3", async () =>
        {
            var r = await T("read_file", ("path", "docs/big.txt"));
            int lines = Seen(r).Split('\n').Length;
            Check("R3", "5,000-line file without limit is capped, not dumped whole", r.Success && lines <= 2200, $"returned {lines} lines, {Seen(r).Length} chars");
            GapIf("R3b", "…and says how to read the rest (offset / total line count)", Seen(r).Contains("5000") || Seen(r).Contains("offset", StringComparison.OrdinalIgnoreCase), Trunc(Seen(r)[^Math.Min(300, Seen(r).Length)..], 300));
        });
        await Case("R4", async () => { var r = await T("read_file", ("path", "docs/big.txt"), ("offset", "4990"), ("limit", "3")); Check("R4", "offset/limit returns the right numbered lines", r.Success && Seen(r).Contains("4990") && Seen(r).Contains("line 4992") && !Seen(r).Contains("line 4993"), Seen(r)); });
        await Case("R5", async () => { var r = await T("read_file", ("path", "App/Thai.cs")); Check("R5", "Thai text survives a read", r.Success && Seen(r).Contains("สวัสดีครับ"), Seen(r)); });
        await Case("R6", async () => { var r = await T("read_file", ("path", "../cluadex-outside.txt")); Check("R6", "path outside the project is refused", !r.Success && !Seen(r).Contains("outside the project"), Seen(r)); });
        await Case("R7", async () => { var r = await T("read_file", ("path", "App")); Check("R7", "reading a directory gives a clear error", !r.Success && Seen(r).Length > 0 && Seen(r).Length < 600, Seen(r)); });
        await Case("R8", async () => { var r = await T("read_file", ("path", "docs/min.js")); Check("R8", "a single 200 KB minified line is capped", Seen(r).Length < 40_000, $"{Seen(r).Length} chars"); });
        await Case("R9", async () => { var r = await T("read_file", ("path", "assets/logo.png")); Check("R9", "binary file → says it's binary instead of dumping bytes", Seen(r).Contains("binary", StringComparison.OrdinalIgnoreCase) && Seen(r).Length < 600, Seen(r)); });

        Console.WriteLine("\n── edit_file ──");
        await Case("E1", async () =>
        {
            await T("read_file", ("path", "App/Calculator.cs"));
            var r = await T("edit_file", ("path", "App/Calculator.cs"),
                ("find", "    public int Add(int a, int b)\n    {\n        return a + b;\n    }"),
                ("replace", "    public int Add(int a, int b)\n    {\n        return checked(a + b);\n    }"));
            string after = Read("App/Calculator.cs");
            Check("E1", "LF 'find' matches a CRLF file (what every model sends on Windows)", r.Success && after.Contains("checked(a + b)"), Seen(r));
            Check("E1b", "…and the file keeps pure CRLF (no mixed line endings)", LoneLf(after) == 0, $"lone LF count = {LoneLf(after)}");
        });
        await Case("E2", async () =>
        {
            var r = await T("edit_file", ("path", "App/Calculator.cs"), ("find", "public int Multiply(int a, int b)"), ("replace", "x"));
            Check("E2", "no match → fails with a message the model can act on", !r.Success && Seen(r).Length > 10, Seen(r));
            GapIf("E2b", "…and shows the closest real text so the model can retry without re-reading", Seen(r).Contains("Add(") || Seen(r).Contains("Subtract(") || Seen(r).Contains("closest", StringComparison.OrdinalIgnoreCase), Seen(r));
        });
        await Case("E3", async () =>
        {
            await T("read_file", ("path", "dup.txt"));
            var r = await T("edit_file", ("path", "dup.txt"), ("find", "foo"), ("replace", "baz"));
            Check("E3", "ambiguous match (2 hits) is refused, not guessed", !r.Success && Read("dup.txt").Contains("foo"), Seen(r));
            GapIf("E3b", "…and the error says how many hits + suggests replace_all / more context", Seen(r).Contains("2") && (Seen(r).Contains("replace_all") || Seen(r).Contains("context", StringComparison.OrdinalIgnoreCase)), Seen(r));
        });
        await Case("E4", async () =>
        {
            var r = await T("edit_file", ("path", "dup.txt"), ("find", "foo"), ("replace", "baz"), ("replace_all", "true"));
            string after = Read("dup.txt");
            Check("E4", "replace_all replaces every occurrence", r.Success && !after.Contains("foo") && after.Split("baz").Length == 3, Seen(r));
        });
        await Case("E5", async () =>
        {
            await T("read_file", ("path", "App/Bom.cs"));
            var r = await T("edit_file", ("path", "App/Bom.cs"), ("find", "Value = 1"), ("replace", "Value = 2"));
            var b = Bytes("App/Bom.cs");
            Check("E5", "editing a UTF-8-BOM file keeps the BOM", r.Success && b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF && Read("App/Bom.cs").Contains("Value = 2"), Seen(r) + $" | first bytes: {BitConverter.ToString(b.Take(4).ToArray())}");
        });
        await Case("E6", async () =>
        {
            WriteCrlf("App/Fresh.cs", "namespace App;\n\npublic static class Fresh\n{\n    public const int N = 1;\n}\n");
            var r = await T("edit_file", ("path", "App/Fresh.cs"), ("find", "N = 1"), ("replace", "N = 7"));
            bool edited = Read("App/Fresh.cs").Contains("N = 7");
            Check("E6", "edit of a never-read file either works or says exactly what to do (read first)", edited || Seen(r).Contains("read", StringComparison.OrdinalIgnoreCase), Seen(r));
        });
        await Case("E7", async () =>
        {
            await T("read_file", ("path", "App/Thai.cs"));
            var r = await T("edit_file", ("path", "App/Thai.cs"), ("find", "\"สวัสดีครับ\""), ("replace", "\"สวัสดีค่ะ\""));
            string after = Read("App/Thai.cs");
            Check("E7", "Thai find/replace round-trips cleanly", r.Success && after.Contains("สวัสดีค่ะ") && !after.Contains('\uFFFD'), Seen(r));
        });
        await Case("E8", async () =>
        {
            await T("read_file", ("path", "App/Calculator.cs"));
            // Model got the indentation wrong (2 spaces instead of 4) — extremely common.
            var r = await T("edit_file", ("path", "App/Calculator.cs"),
                ("find", "  public int Subtract(int a, int b)\n  {\n    return a - b;\n  }"),
                ("replace", "  public int Subtract(int a, int b)\n  {\n    return a - b - 0;\n  }"));
            string after = Read("App/Calculator.cs");
            string line = after.Split('\n').FirstOrDefault(l => l.Contains("a - b - 0")) ?? "";
            Check("E8", "wrong-indentation 'find' still lands (whitespace-tolerant, as the schema promises)", r.Success && line.Length > 0, Seen(r));
            GapIf("E8b", "…and the new code adopts the FILE's indentation, not the model's", line.StartsWith("        return"), $"line = [{line.TrimEnd('\r')}]");
        });
        await Case("E9", async () =>
        {
            var r = await T("edit_file", ("path", "App/Calculator.cs"), ("find", "namespace App;"), ("replace", "namespace App;"));
            GapIf("E9", "find == replace is reported as a no-op, not a successful edit", !r.Success || Seen(r).Contains("identical", StringComparison.OrdinalIgnoreCase) || Seen(r).Contains("no change", StringComparison.OrdinalIgnoreCase), Seen(r));
        });
        await Case("E10", async () =>
        {
            await T("read_file", ("path", "App/Program.cs"));
            File.AppendAllText(P("App", "Program.cs"), "// changed by someone else\r\n");   // e.g. the user saved in the editor
            var r = await T("edit_file", ("path", "App/Program.cs"), ("find", "calc.Add(2, 3)"), ("replace", "calc.Add(4, 5)"));
            GapIf("E10", "edit after the file changed on disk is flagged (stale read)", !r.Success || Seen(r).Contains("changed", StringComparison.OrdinalIgnoreCase) || Seen(r).Contains("modified", StringComparison.OrdinalIgnoreCase), Seen(r));
        });
        await Case("E11", async () => { var r = await T("edit_file", ("path", "App/Nope.cs"), ("find", "a"), ("replace", "b")); Check("E11", "edit of a missing file → clear error", !r.Success && Seen(r).Contains("Nope.cs"), Seen(r)); });
        await Case("E12", async () =>
        {
            await T("read_file", ("path", "run.sh"));
            var r = await T("edit_file", ("path", "run.sh"), ("find", "echo hello"), ("replace", "echo hi"));
            var b = Bytes("run.sh");
            Check("E12", "editing a BOM-less file does NOT add a BOM (shebang must stay first)", r.Success && b[0] == (byte)'#', $"first bytes: {BitConverter.ToString(b.Take(4).ToArray())}");
        });
        await Case("E13", async () =>
        {
            var before = Bytes("App/Legacy.cs");
            var r = await T("edit_file", ("path", "App/Legacy.cs"), ("find", "public class Legacy { }"), ("replace", "public class Legacy2 { }"));
            Check("E13", "a legacy-encoded (TIS-620) file is refused, not silently corrupted", Bytes("App/Legacy.cs").SequenceEqual(before) && !r.Success, Seen(r));
        });
        await Case("E14", async () =>
        {
            await T("read_file", ("path", "App/Program.cs"));
            // Models paste the read_file line-number prefix into 'find' — very common with numbered reads.
            var r = await T("edit_file", ("path", "App/Program.cs"), ("find", "7\t        var calc = new Calculator();"), ("replace", "7\t        var calc = new Calculator(); // made3"));
            Check("E14", "'find' pasted WITH line-number prefixes still lands, prefixes not written", r.Success && Read("App/Program.cs").Contains("// made3") && !Read("App/Program.cs").Contains("7\t"), Seen(r));
        });

        Console.WriteLine("\n── multi_edit ──");
        await Case("M1", async () =>
        {
            await T("read_file", ("path", "App/Program.cs"));
            string edits = "[{\"find\":\"var calc = new Calculator();\",\"replace\":\"var calc = new Calculator(); // made\"},{\"find\":\"Console.WriteLine(\",\"replace\":\"System.Console.WriteLine(\"}]";
            var r = await T("multi_edit", ("path", "App/Program.cs"), ("edits", edits));
            string after = Read("App/Program.cs");
            Check("M1", "two edits in one call both apply", r.Success && after.Contains("// made") && after.Contains("System.Console.WriteLine("), Seen(r));
        });
        await Case("M2", async () =>
        {
            string before = Read("App/Program.cs");
            string edits = "[{\"find\":\"// made\",\"replace\":\"// made twice\"},{\"find\":\"THIS DOES NOT EXIST\",\"replace\":\"x\"}]";
            var r = await T("multi_edit", ("path", "App/Program.cs"), ("edits", edits));
            Check("M2", "one failing edit → nothing written (atomic)", !r.Success && Read("App/Program.cs") == before, Seen(r));
        });

        Console.WriteLine("\n── write_file ──");
        await Case("W1", async () => { var r = await T("write_file", ("path", "App/Deep/Nested/New.cs"), ("content", "namespace App.Deep.Nested;\n\npublic static class New { }\n")); Check("W1", "creates a file and its missing parent folders", r.Success && File.Exists(P("App", "Deep", "Nested", "New.cs")), Seen(r)); });
        await Case("W2", async () => { var r = await T("write_file", ("path", "../cluadex-evil.txt"), ("content", "x")); Check("W2", "write outside the project is refused", !r.Success && !File.Exists(Path.Combine(Path.GetDirectoryName(Root)!, "cluadex-evil.txt")), Seen(r)); });
        await Case("W3", async () =>
        {
            WriteCrlf("App/Precious.cs", "namespace App;\n\n// 400 lines of the user's work would be here\npublic static class Precious { }\n");
            var r = await T("write_file", ("path", "App/Precious.cs"), ("content", "// oops\n"));
            GapIf("W3", "blind overwrite of a never-read file keeps a recoverable backup", Seen(r).Contains("backup", StringComparison.OrdinalIgnoreCase) || Seen(r).Contains("saved", StringComparison.OrdinalIgnoreCase), Seen(r));
        });
        await Case("W4", async () =>
        {
            var r = await T("write_file", ("path", "tools/new.sh"), ("content", "#!/bin/bash\necho new\n"));
            var b = Bytes("tools/new.sh");
            Check("W4", "a NEW file is written without a BOM", r.Success && b[0] == (byte)'#', $"first bytes: {BitConverter.ToString(b.Take(4).ToArray())}");
        });
        await Case("W5", async () =>
        {
            await T("read_file", ("path", "App/Calculator.cs"));
            string whole = Read("App/Calculator.cs");
            var r = await T("write_file", ("path", "App/Calculator.cs"), ("content", "namespace App;\n\npublic class Calculator\n{\n    // ... existing code ...\n\n    public int Multiply(int a, int b) => a * b;\n}\n"));
            Check("W5", "whole-file write containing '// ... existing code ...' is refused (it would delete that code)", !r.Success && Read("App/Calculator.cs") == whole, Seen(r));
            if (Read("App/Calculator.cs") != whole) File.WriteAllText(P("App", "Calculator.cs"), whole, new UTF8Encoding(false));   // keep later cases honest
        });
        string backups = Path.Combine(settings.DataRoot, "file-backups");
        await Case("W6", async () =>
        {
            // The model read the TIS-620 file (seeing �) and sends it back with one change.
            File.WriteAllBytes(P("App", "Legacy2.cs"), Encoding.ASCII.GetBytes("// ").Concat(new byte[] { 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5 }).Concat(Encoding.ASCII.GetBytes("\r\npublic class Legacy2 { }\r\n")).ToArray());
            var before = Bytes("App/Legacy2.cs");
            await T("read_file", ("path", "App/Legacy2.cs"));
            var r = await T("write_file", ("path", "App/Legacy2.cs"), ("content", "// ������\npublic class Legacy2 { int x; }\n"));
            Check("W6", "write_file of content still carrying � over a TIS-620 file is refused", !r.Success && Bytes("App/Legacy2.cs").SequenceEqual(before), Seen(r));
            var ok = await T("write_file", ("path", "App/Legacy2.cs"), ("content", "// สวัสดี\npublic class Legacy2 { }\n"));
            var saved = Directory.Exists(backups) ? Directory.GetFiles(backups, "*Legacy2.cs").OrderBy(f => f).LastOrDefault() : null;
            Check("W6b", "…a clean rewrite is allowed, and the original bytes are backed up", ok.Success && Read("App/Legacy2.cs").Contains("สวัสดี") && saved != null && File.ReadAllBytes(saved).SequenceEqual(before), Seen(ok));
            File.Delete(P("App", "Legacy2.cs"));
        });
        await Case("W7", async () =>
        {
            // 1.3 MB, never read by the agent.
            var big = string.Join("\n", Enumerable.Range(1, 60_000).Select(i => $"row {i:D6} of the data set"));
            File.WriteAllText(P("docs", "huge.txt"), big, new UTF8Encoding(false));
            var r = await T("write_file", ("path", "docs/huge.txt"), ("content", "tiny\n"));
            var saved = Directory.Exists(backups) ? Directory.GetFiles(backups, "*huge.txt").FirstOrDefault() : null;
            Check("W7", "overwriting a >1 MB file is reported as an overwrite and backed up (not \"Created\")", r.Success && Seen(r).StartsWith("Overwrote") && saved != null && new FileInfo(saved).Length > 1_000_000, Seen(r));
            string year = DateTime.Now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture);
            Check("W7b", $"backups live in the app's data root, named with the Gregorian year ({year}, not 2569)", saved != null && Path.GetFileName(saved).StartsWith(year), saved ?? "(none)");
            File.Delete(P("docs", "huge.txt"));
        });

        Console.WriteLine("\n── grep_search ──");
        await Case("G1", async () => { var r = await T("grep_search", ("pattern", "class Calculator")); Check("G1", "finds the defining file (default mode = files_with_matches)", r.Success && Seen(r).Contains("App/Calculator.cs") && !Seen(r).Contains("secret.cs"), Seen(r)); });
        await Case("G2", async () =>
        {
            var r = await T("grep_search", ("pattern", "Calculator"), ("output_mode", "files_with_matches"));
            Check("G2", "skips bin/ and obj/ build output", r.Success && !Seen(r).Contains("junk.cs"), Seen(r));
            GapIf("G2b", "respects .gitignore (ignored/secret.cs not listed)", !Seen(r).Contains("secret.cs"), Seen(r));
        });
        await Case("G3", async () => { var r = await T("grep_search", ("pattern", "namespace"), ("glob", "*.cs")); Check("G3", "glob filter restricts to *.cs", r.Success && Seen(r).Contains(".cs") && !Seen(r).Contains("App.csproj") && !Seen(r).Contains("big.txt"), Seen(r)); });
        await Case("G4", async () => { var r = await T("grep_search", ("pattern", "public"), ("output_mode", "count")); Check("G4", "output_mode=count gives per-file counts", r.Success && System.Text.RegularExpressions.Regex.IsMatch(Seen(r), @"\d"), Seen(r)); });
        await Case("G5", async () => { var r = await T("grep_search", ("pattern", "return a - b"), ("context", "2")); Check("G5", "context lines are included", r.Success && Seen(r).Contains("Subtract"), Seen(r)); });
        await Case("G6", async () => { var r = await T("grep_search", ("pattern", "Calc(")); Check("G6", "invalid regex → clear error (no crash, no stack trace)", !r.Success && !Seen(r).Contains("   at "), Seen(r)); });
        await Case("G7", async () =>
        {
            var r = await T("grep_search", ("pattern", "line"), ("path", "docs"));
            Check("G7", "5,000 matches are capped with a notice, not dumped into context", r.Success && Seen(r).Length < 60000, $"{Seen(r).Length} chars");
        });
        await Case("G8", async () =>
        {
            Fs.WorkingDirectory = @"E:\Code\ClaudeClient";           // read-only search over the real repo
            var r = await T("grep_search", ("pattern", "ExecuteToolAsync"), ("output_mode", "files_with_matches"));
            long ms = LastMs;
            Fs.WorkingDirectory = Root;
            Check("G8", $"search over the real CluadeX repo is fast ({ms} ms)", r.Success && Seen(r).Contains("AgentToolService.cs") && ms < 3000, Seen(r));
        });

        await Case("G9", async () =>
        {
            var r = await T("grep_search", ("pattern", "return a"), ("output_mode", "content"));
            Check("G9", "content mode prints path:line:text", r.Success && System.Text.RegularExpressions.Regex.IsMatch(Seen(r), @"App/Calculator\.cs:\d+:\s+return a"), Seen(r));
        });
        await Case("G10", async () =>
        {
            var r = await T("grep_search", ("pattern", "Greeting"), ("type", "cs"), ("output_mode", "content"));
            Check("G10", "type=cs filter", r.Success && Seen(r).Contains("Thai.cs") && !Seen(r).Contains(".md"), Seen(r));
        });

        Console.WriteLine("\n── glob ──");
        await Case("GL1", async () =>
        {
            var r = await T("glob", ("pattern", "**/*.cs"));
            Check("GL1", "**/*.cs finds nested files", r.Success && Seen(r).Contains("Calculator.cs") && Seen(r).Contains("New.cs"), Seen(r));
            Check("GL1b", "…and skips bin/ obj/", !Seen(r).Contains("junk.cs"), Seen(r));
        });
        await Case("GL2", async () =>
        {
            File.SetLastWriteTimeUtc(P("App", "Thai.cs"), DateTime.UtcNow.AddMinutes(5));
            var r = await T("glob", ("pattern", "**/*.cs"));
            var firstLine = Seen(r).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(".cs")) ?? "";
            GapIf("GL2", "results sorted most-recently-modified first", firstLine.EndsWith("Thai.cs"), "first = " + firstLine);
        });

        Console.WriteLine("\n── run_command (phrased the way models phrase it) ──");
        await Case("C1", async () => { var r = await T("run_command", ("command", "dotnet --version")); Check("C1", "dotnet --version", r.Success && System.Text.RegularExpressions.Regex.IsMatch(Seen(r), @"\d+\.\d+"), Seen(r)); });
        foreach (var (id, cmd, expect) in new[]
        {
            ("C2", "ls", "App"),
            ("C3", "cat App/Program.cs", "calc.Add"),
            ("C5", "mkdir -p tmp/a/b && echo made", "made"),
            ("C6", "pwd", "cluadex-tooltest"),
            ("C7", "grep -rn \"class Calculator\" App --include=*.cs", "Calculator.cs"),
            ("C8", "echo สวัสดี", "สวัสดี"),
            ("C9", "git status --short", ""),
            ("C10", "head -n 3 docs/big.txt", "line 3"),
            ("C11", "wc -l docs/big.txt", "5000"),
            ("C12", "rm -rf tmp && echo gone", "gone"),
            ("C4", "cd App && dotnet build --nologo -v q", "0 Error"),
        })
        {
            await Case(id, async () =>
            {
                var r = await T("run_command", ("command", cmd), ("timeout_ms", "120000"));
                Check(id, $"`{cmd}`", r.Success && (expect.Length == 0 || Seen(r).Contains(expect)), Seen(r));
            });
        }
        await Case("C13", async () => { var r = await T("run_command", ("command", "exit 3")); Check("C13", "non-zero exit → Success=false and the exit code is shown", !r.Success && Seen(r).Contains("3"), Seen(r)); });
        await Case("C14", async () => { var r = await T("run_command", ("command", "foobarxyz_not_a_command")); Check("C14", "unknown command → failure with the shell's message", !r.Success && Seen(r).Length > 0, Seen(r)); });
        await Case("C15", async () =>
        {
            var r = await T("run_command", ("command", "ping -n 20 127.0.0.1"), ("timeout_ms", "2000"));
            Check("C15", $"timeout_ms=2000 actually stops a 20 s command ({LastMs} ms)", LastMs < 8000 && !r.Success, Seen(r));
        });
        await Case("C16", async () =>
        {
            var r = await T("run_command", ("command", "git log --oneline -n 1 && dotnet --list-sdks"), ("timeout_ms", "60000"));
            Check("C16", "`&&` chaining works", r.Success && Seen(r).Contains("init"), Seen(r));
        });
        await Case("C17", async () =>
        {
            await T("run_command", ("command", "cd App"));
            var r = await T("run_command", ("command", "dir /b *.cs || ls *.cs"));
            GapIf("C17", "working directory persists across calls (cd App, then list)", Seen(r).Contains("Program.cs"), Seen(r));
        });
        await Case("C21", async () =>
        {
            var r = await T("run_command", ("command", "cat docs/big.txt | head -1"));
            Check("C21", "a root-relative path failing in a cd'd shell says where it ran + how to get back", !r.Success || Seen(r).Contains("line 1") ? (Seen(r).Contains("cd") || Seen(r).Contains("line 1")) : true, Seen(r));
            var back = await T("run_command", ("command", "cd"));
            var here = await T("run_command", ("command", "ls docs"));
            Check("C21b", "a bare `cd` returns to the project root", Seen(here).Contains("big.txt") && !Seen(here).Contains("[cwd:"), Seen(back) + " || " + Seen(here));
        });
        await Case("C18", async () =>
        {
            var r = await T("run_command", ("command", "git log --all --oneline && type docs\\big.txt || cat docs/big.txt"), ("timeout_ms", "60000"));
            Check("C18", "large output (5,000 lines) is capped with a notice", Seen(r).Length < 60000, $"{Seen(r).Length} chars");
        });

        await Case("C19", async () =>
        {
            var r = await T("run_command", ("command", "git log --oneline >/dev/null 2>&1; for i in $(seq 1 4000); do echo \"noise line $i\"; done; echo 'Program.cs(7,9): error CS0103: FINAL_ERROR_AT_END'"), ("timeout_ms", "60000"));
            Check("C19", "long output keeps the END (where build/test errors land)", Seen(r).Contains("FINAL_ERROR_AT_END"), Trunc(Seen(r)[^Math.Min(200, Seen(r).Length)..], 200));
        });
        await Case("C20", async () =>
        {
            var r = await T("run_command", ("command", "echo STARTED_OK && ping -n 20 127.0.0.1"), ("timeout_ms", "3000"));
            Check("C20", "a timed-out command still returns what it printed", Seen(r).Contains("STARTED_OK"), Seen(r));
        });

        Console.WriteLine("\n── powershell ──");
        await Case("P1", async () => { var r = await T("powershell", ("command", "Get-ChildItem App -Filter *.cs -Name")); Check("P1", "Get-ChildItem", r.Success && Seen(r).Contains("Program.cs"), Seen(r)); });
        await Case("P2", async () => { var r = await T("powershell", ("command", "Write-Output 'สวัสดี'")); Check("P2", "Thai output intact", r.Success && Seen(r).Contains("สวัสดี"), Seen(r)); });

        Console.WriteLine("\n── run_build ──");
        await Case("B1", async () => { var r = await T("run_build"); Check("B1", $"builds the project ({LastMs} ms)", r.Success, Seen(r)); });
        await Case("B2", async () =>
        {
            File.WriteAllText(P("App", "Broken.cs"), "namespace App;\n\npublic class Broken\n{\n    int x = ;\n}\n");
            var r = await T("run_build");
            Check("B2", "a compile error → failure naming file, line and CS code", !r.Success && Seen(r).Contains("App/Broken.cs(5") && System.Text.RegularExpressions.Regex.IsMatch(Seen(r), @"CS\d{4}"), Seen(r));
            GapIf("B2b", "…distilled (under 3,000 chars, each error once), not the full MSBuild log", Seen(r).Length < 3000 && Seen(r).Split("Broken.cs(5").Length == 2, $"{Seen(r).Length} chars");
            File.Delete(P("App", "Broken.cs"));
        });

        Console.WriteLine("\n── run_tests ──");
        await Case("T1", async () => { var r = await T("run_tests"); Check("T1", "no test project → a clear message, not a hang", LastMs < 60000 && Seen(r).Length > 0, Seen(r)); });

        Console.WriteLine("\n── todo_write ──");
        await Case("TD1", async () =>
        {
            var a = await T("todo_write", ("action", "add"), ("content", "wire the parser"));
            var l = await T("todo_write", ("action", "list"));
            Check("TD1", "add + list", a.Success && Seen(l).Contains("wire the parser"), Seen(l));
            var c = await T("todo_write", ("action", "complete"), ("content", "wire the parser"));
            var l2 = await T("todo_write", ("action", "list"));
            Check("TD2", "complete marks it done", c.Success && (Seen(l2).Contains("✓") || Seen(l2).Contains("[x]") || Seen(l2).Contains("completed", StringComparison.OrdinalIgnoreCase)), Seen(l2));
        });

        // The human-facing writers share the agent's encoding rules: the editor's Save and the IDE's
        // rename both used to rewrite files with Encoding.UTF8 (BOM added) or ReadAllText (BOM dropped).
        Console.WriteLine("\n── editor Save / IDE rename ──");
        var ws = provider.GetRequiredService<CodeWorkspaceService>();
        await Case("X1", async () =>
        {
            var tab = await ws.OpenFileAsync(P("App", "Program.cs"));
            tab!.Content = tab.Content.Replace("namespace App;", "namespace App; // saved");
            bool ok = await ws.SaveTabAsync(tab);
            var b = Bytes("App/Program.cs");
            Check("X1", "editor Save of a no-BOM CRLF file adds no BOM and keeps CRLF", ok && b[0] != 0xEF && LoneLf(Read("App/Program.cs")) == 0 && Read("App/Program.cs").Contains("// saved"), $"ok={ok} first={BitConverter.ToString(b.Take(3).ToArray())} lf={LoneLf(Read("App/Program.cs"))}");
            File.WriteAllText(P("App", "Program.cs"), Read("App/Program.cs").Replace(" // saved", ""), new UTF8Encoding(false));
        });
        await Case("X2", async () =>
        {
            var tab = await ws.OpenFileAsync(P("App", "Bom.cs"));
            tab!.Content += "// touched\r\n";
            bool ok = await ws.SaveTabAsync(tab);
            var b = Bytes("App/Bom.cs");
            Check("X2", "editor Save of a BOM file keeps exactly one BOM", ok && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF && b[3] != 0xEF, BitConverter.ToString(b.Take(6).ToArray()));
        });
        await Case("X3", async () =>
        {
            var before = Bytes("App/Legacy.cs");
            var tab = await ws.OpenFileAsync(P("App", "Legacy.cs"));
            bool ok = tab != null && await ws.SaveTabAsync(tab);
            Check("X3", "editor Save refuses a TIS-620 file instead of corrupting it", !ok && Bytes("App/Legacy.cs").SequenceEqual(before), $"opened={tab != null} saved={ok}");
        });
        await Case("X4", async () =>
        {
            Directory.CreateDirectory(P("Ren"));
            File.WriteAllText(P("Ren", "A.cs"), "class RenameMe { }\r\n", new UTF8Encoding(true));
            File.WriteAllText(P("Ren", "B.cs"), "class UsesIt { RenameMe x; }\n", new UTF8Encoding(false));
            // "RenameMe" in ASCII, then a TIS-620 comment (สวัสดี) — valid Windows-874, invalid UTF-8.
            var legacy = Encoding.ASCII.GetBytes("class Old { RenameMe y; } // ").Concat(new byte[] { 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5, 0x0A }).ToArray();
            File.WriteAllBytes(P("Ren", "C.cs"), legacy);
            var intel = provider.GetRequiredService<CodeIntelligenceService>();
            var plan = await intel.PrepareRenameAsync("RenameMe", "Renamed");
            var res = await intel.ApplyRenameAsync(plan);
            var a = Bytes("Ren/A.cs"); var bb = Bytes("Ren/B.cs");
            Check("X4", "IDE rename keeps a BOM file's BOM", a[0] == 0xEF && a[3] != 0xEF && Read("Ren/A.cs").Contains("Renamed"), $"err={plan.Error} files={plan.Files.Count} first={BitConverter.ToString(a.Take(4).ToArray())}");
            Check("X4b", "…adds no BOM to a plain file", bb[0] != 0xEF && Read("Ren/B.cs").Contains("Renamed"), BitConverter.ToString(bb.Take(4).ToArray()));
            Check("X4c", "…and leaves a TIS-620 file byte-for-byte untouched", Bytes("Ren/C.cs").SequenceEqual(legacy), "skipped: " + string.Join(", ", res.Skipped));
            Directory.Delete(P("Ren"), true);
        });

        // The descriptions are the model's manual — a stale one (a 30 s timeout that is really 120 s, a
        // POSIX example on a cmd.exe box) is a bug the model acts on, so hold them to the behaviour.
        Console.WriteLine("\n── tool descriptions ──");
        await Case("S1", async () =>
        {
            var all = Svc.BuildNativeToolSchemas().ToDictionary(s => s.Name);
            string shell = ShellRunner.DefaultShell == ShellRunner.Shell.Bash ? "Git Bash" : "cmd.exe";
            Check("S1", $"run_command's description names the shell that really runs ({shell}) and the real timeout", all["run_command"].Description.Contains(shell) && all["run_command"].Description.Contains("120 s") && !System.Text.Json.JsonSerializer.Serialize(all["run_command"].InputSchema).Contains("30000"), all["run_command"].Description);
            Check("S1b", "read_file no longer promises the whole file", !all["read_file"].Description.Contains("whole file") && all["read_file"].Description.Contains("offset"), all["read_file"].Description);
            string props = System.Text.Json.JsonSerializer.Serialize(all["grep_search"].InputSchema);
            Check("S1c", "grep_search advertises type / ignore_case / head_limit and the files_with_matches default", props.Contains("\"type\"") && props.Contains("ignore_case") && props.Contains("head_limit") && all["grep_search"].Description.Contains("files_with_matches (default"), props);
            var legacy = Svc.GetToolDefinitionsPrompt();
            Check("S1d", "legacy [ACTION:] catalogue renders the shell note and literal braces", legacy.Contains(shell) && legacy.Contains("*.{cs,xaml}") && !legacy.Contains("{shellNote}"), Trunc(legacy[Math.Max(0, legacy.IndexOf("7. run_command"))..], 400));
            await Task.CompletedTask;
        });
        await Case("S2", async () =>
        {
            // Every advertised grep_search key must actually steer the executor.
            var few = await T("grep_search", ("pattern", "namespace"), ("head_limit", "1"));
            int listed = Seen(few).Split('\n').Count(l => l.EndsWith(".cs"));
            Check("S2", "head_limit=1 returns one file", listed == 1, Seen(few));
            var exact = await T("grep_search", ("pattern", "namespace app"), ("ignore_case", "false"), ("output_mode", "count"));
            Check("S2b", "ignore_case=false turns smart case off (lowercase 'namespace app' ≠ 'namespace App')", Seen(exact).Contains("0 match") || Seen(exact).StartsWith("No match"), Seen(exact));
        });

        string reportPath = Path.Combine(AppContext.BaseDirectory, "tool-report.md");
        File.WriteAllText(reportPath, Report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL · {Gap} GAP ═══");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        Console.WriteLine($"\nreport : {reportPath}\nproject: {Root}");
        return Fail == 0 ? 0 : 1;
    }
}
