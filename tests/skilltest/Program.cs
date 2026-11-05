// skilltest — the Skills system end to end, offline, against the REAL CluadeX.dll:
//   P* the frontmatter parser (CluadeX keys and the Anthropic / Claude Code spelling)
//   D* discovery and the cache following the disk (a skill saved seconds ago is found)
//   E* switching a skill off: gone from the prompt list, refused by skill_invoke, counted
//   I* installing from a trusted source through a fake network (no request leaves this PC)
//   V* the Skills page view model and its localisation keys
//
// Build CluadeX first (the app may be running: build it with -p:OutDir=<dir>\), then:
//   dotnet run --project tests/skilltest -p:CluadeXBin=<dir>\
// portable.txt keeps every file inside this harness's bin folder; the owner's ~/.cluadex is never touched.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

int pass = 0, fail = 0;
void Check(string id, string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS {id} {what}"); }
    else { fail++; Console.WriteLine($"FAIL {id} {what} :: {detail}"); }
}

var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
var services = new ServiceCollection();
configure.Invoke(null, new object[] { services });
var provider = services.BuildServiceProvider();
var settings = provider.GetRequiredService<SettingsService>();
if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("ABORT: data root is not isolated (portable.txt missing): " + settings.DataRoot);
    return 3;
}
var loc = provider.GetRequiredService<LocalizationService>();
var fs = provider.GetRequiredService<FileSystemService>();
var skills = provider.GetRequiredService<SkillService>();
var tools = provider.GetRequiredService<AgentToolService>();

string scratch = Path.Combine(AppContext.BaseDirectory, "scratch-" + DateTime.Now.ToString("HHmmssfff"));
string userDir = Path.Combine(scratch, "user-skills");
string proj = Path.Combine(scratch, "proj");
Directory.CreateDirectory(userDir);
Directory.CreateDirectory(proj);
skills.UserSkillsDirectory = userDir;
fs.WorkingDirectory = proj;
settings.Settings.DisabledSkills.Clear();
settings.Settings.TrustedSkillSources = new() { "https://github.com/anthropics/skills" };
File.Delete(Path.Combine(settings.DataRoot, "skill-usage.json"));

// The owner says yes to every permission prompt (the real prompt is a dialog).
tools.OnPermissionRequired += _ => Task.FromResult(true);

Task<ToolResult> Invoke(string skill, string args = "") => tools.ExecuteToolAsync(new ToolCall
{
    Type = ToolType.SkillInvoke, ToolName = "skill_invoke",
    Arguments = new() { ["skill"] = skill, ["args"] = args },
}, CancellationToken.None);
string Seen(ToolResult r) => r.Success ? r.Output : r.Error + " " + r.Output;

// ─── P: parser ───────────────────────────────────────────────────────────
{
    var s = SkillService.ParseSkillContent("""
        ---
        name: brand-guidelines
        description: "Applies the brand colours. Use it when style guidelines apply."
        license: Complete terms in LICENSE.txt
        allowed-tools: Read, Grep, Bash(git status:*), FooTool
        when_to_use: the user asks for on-brand slides
        argument-hint: [file]
        user-invocable: false
        ---

        # Brand
        Body text.
        """.Replace("        ", ""), "folder-name");
    Check("P1", "Anthropic keys: name, quoted description, license", s.Name == "brand-guidelines"
        && s.Description.StartsWith("Applies the brand") && !s.Description.Contains('"') && s.License == "Complete terms in LICENSE.txt", s.Description);
    Check("P2", "allowed-tools with Claude Code names map to CluadeX tools", string.Join(",", s.AllowedTools) == "read_file,grep,run_command",
        string.Join(",", s.AllowedTools));
    Check("P3", "an unknown tool is reported, not silently dropped", s.UnknownTools.SequenceEqual(new[] { "FooTool" }), string.Join(",", s.UnknownTools));
    Check("P4", "when_to_use / argument-hint / user-invocable", s.WhenToUse == "the user asks for on-brand slides"
        && s.ArgumentHint == "[file]" && !s.UserInvocable);
    Check("P5", "the body below the frontmatter is the prompt", s.PromptContent.StartsWith("# Brand") && s.PromptContent.Contains("Body text."));

    var b = SkillService.ParseSkillContent("---\r\ndescription: >\r\n  Folded over\r\n  two lines\r\nallowedTools:\r\n  - read_file\r\n  - Write\r\n---\r\nDo it.\r\n", "fallback");
    Check("P6", "CRLF, block scalar description, YAML list, fallback name", b.Name == "fallback" && b.Description == "Folded over two lines"
        && string.Join(",", b.AllowedTools) == "read_file,write_file" && b.PromptContent == "Do it.",
        $"{b.Name}|{b.Description}|{string.Join(",", b.AllowedTools)}|{b.PromptContent}");
    var old = SkillService.ParseSkillContent("---\nname: x\nwhenToUse: old key\nallowedTools: [read_file, \"grep\"]\n---\nbody", "x");
    Check("P7", "the old CluadeX spelling still works", old.WhenToUse == "old key" && string.Join(",", old.AllowedTools) == "read_file,grep");
    var none = SkillService.ParseSkillContent("just text", "plain");
    Check("P8", "no frontmatter: whole file is the prompt", none.Name == "plain" && none.PromptContent == "just text");
}

// ─── D: discovery + cache follows the disk ──────────────────────────────
{
    int before = skills.GetAllSkills().Count;
    Check("D1", "19 built-ins and nothing else in an empty folder", before == 19 && skills.GetAllSkills().All(s => s.IsBuiltIn), before.ToString());

    File.WriteAllText(Path.Combine(userDir, "tool-precision.md"), "---\nname: tool-precision\ndescription: Pick the right tool\nwhenToUse: before a shell call\n---\nCheck first.");
    // The 2026-11-05 14:44 case: the list was cached before the file existed, then skill_invoke asked for it.
    var r = await Invoke("tool-precision");
    Check("D2", "skill_invoke finds a skill saved after the list was cached", r.Success && r.Output.Contains("# Executing Skill: tool-precision"), Seen(r));

    Directory.CreateDirectory(Path.Combine(userDir, "pdf-tools"));
    File.WriteAllText(Path.Combine(userDir, "pdf-tools", "SKILL.md"), "---\nname: pdf-tools\ndescription: PDF things\n---\nUse pypdf.");
    var all = skills.GetAllSkills();
    Check("D3", "the Anthropic folder layout name/SKILL.md is discovered, by GetAllSkills alone (no reload call)",
        all.Any(s => s.Name == "pdf-tools" && s.Source == SkillSource.User), string.Join(",", all.Select(s => s.Name)));

    string projSkills = Path.Combine(proj, ".cluadex", "skills");
    Directory.CreateDirectory(projSkills);
    File.WriteAllText(Path.Combine(projSkills, "commit.md"), "---\nname: commit\ndescription: Project commit rules\n---\nOur way.");
    var commit = skills.GetSkillByName("commit");
    Check("D4", "a project skill overrides the built-in of the same name", commit is { Source: SkillSource.Project, IsBuiltIn: false }
        && commit.Description == "Project commit rules", commit?.Description ?? "null");
    File.Delete(Path.Combine(projSkills, "commit.md"));
    Check("D5", "deleting it brings the built-in back", skills.GetSkillByName("commit")?.IsBuiltIn == true);
}

// ─── E: on / off ─────────────────────────────────────────────────────────
{
    Check("E1", "everything starts switched on", skills.GetAllSkills().All(s => skills.IsEnabled(s.Name)));
    bool saved = skills.SetEnabled("deep-research", false);
    Check("E2", "switching off is saved in settings", saved && settings.Settings.DisabledSkills.Contains("deep-research"));
    var settingsJson = File.ReadAllText(Path.Combine(settings.DataRoot, "settings.json"));
    Check("E3", "… on disk, under DisabledSkills", Regex.IsMatch(settingsJson, "\"DisabledSkills\"\\s*:\\s*\\[\\s*\"deep-research\""), "");
    Check("E4", "a switched-off skill is not advertised to the model", !tools.GetAdvertisedSkills().Any(s => s.Name == "deep-research")
        && !tools.GetAvailableSkillNames().Any(s => s.Name == "deep-research")
        && tools.GetAdvertisedSkills().Any(s => s.Name == "tool-precision"));
    var r = await Invoke("deep-research", "why");
    Check("E5", "skill_invoke refuses it and says why", !r.Success && Seen(r).Contains("switched off"), Seen(r));
    var list = await Invoke("");
    Check("E6", "the listing (no name) leaves it out", list.Success && !list.Output.Contains("deep-research") && list.Output.Contains("tool-precision"), list.Output);
    var u = skills.GetUsage("deep-research");
    var tp = skills.GetUsage("tool-precision");
    Check("E7", "usage: one refusal counted, one tool start counted", u.Refused == 1 && u.Total == 0 && tp.Tool == 1 && tp.LastUsed != null,
        $"dr refused={u.Refused} total={u.Total} tp tool={tp.Tool}");
    Check("E8", "usage lives in the data root, not ~/.cluadex", File.Exists(Path.Combine(settings.DataRoot, "skill-usage.json")));
    skills.SetEnabled("DEEP-RESEARCH", true);
    Check("E9", "switching back on (any case) clears it", skills.IsEnabled("deep-research") && settings.Settings.DisabledSkills.Count == 0);
    r = await Invoke("deep-research");
    Check("E10", "and skill_invoke runs it again", r.Success, Seen(r));
    tools.ActiveSkillAllowedTools = null;
}

// ─── I: install from a trusted source (fake network) ────────────────────
{
    var fake = new FakeGitHub();
    var installer = new SkillInstaller(settings, skills, fake);
    Check("I0", "DI builds the installer", provider.GetService<SkillInstaller>() != null);

    var src = SkillInstaller.ParseSource("https://github.com/anthropics/skills");
    Check("I1", "repo root → branch main, path skills", src is { Owner: "anthropics", Repo: "skills", Branch: "main", Path: "skills" });
    Check("I2", "only https github.com folders parse", SkillInstaller.ParseSource("http://github.com/a/b") == null
        && SkillInstaller.ParseSource("https://evil.com/anthropics/skills") == null
        && SkillInstaller.ParseSource("https://github.com/a/b/tree/main/../../x") == null
        && SkillInstaller.ParseSource("https://github.com/a/b/blob/main/x.md") == null);
    Check("I3", "trust: same repo + branch + a path at or below the entry", installer.TrustedMatch(src!) != null
        && installer.TrustedMatch(SkillInstaller.ParseSource("https://github.com/anthropics/skills/tree/main/skills/pdf")!) != null
        && installer.TrustedMatch(SkillInstaller.ParseSource("https://github.com/anthropics/skillz")!) == null
        && installer.TrustedMatch(SkillInstaller.ParseSource("https://github.com/anthropics/skills/tree/dev/skills")!) == null
        && installer.TrustedMatch(SkillInstaller.ParseSource("https://github.com/anthropics/skills/tree/main/skillsX")!) == null);

    bool refused = false;
    try { await installer.ListAsync("https://github.com/someone/skills"); } catch (InvalidOperationException) { refused = true; }
    Check("I4", "an untrusted source is refused before any request", refused && fake.Requests.Count == 0, string.Join(" ", fake.Requests));

    var list = await installer.ListAsync("https://github.com/anthropics/skills");
    Check("I5", "listing: folders become skills, README and a bad name are skipped",
        string.Join(",", list.Select(r => r.Name)) == "brand-guidelines,commit,no-front,risky", string.Join(",", list.Select(r => r.Name)));

    var brand = await installer.PreviewAsync(list.First(r => r.Name == "brand-guidelines"));
    Check("I6", "preview: parsed, installable, license shown", brand.CanInstall && brand.Skill.License == "Complete terms in LICENSE.txt"
        && brand.Skill.SourceUrl == "https://github.com/anthropics/skills/tree/main/skills/brand-guidelines", string.Join("; ", brand.Problems));
    Check("I7", "preview warns: no allowed-tools, and 2 extra files left behind (never run)",
        brand.Warnings.Any(w => w.Contains("every tool")) && brand.Warnings.Any(w => w.StartsWith("2 other file")), string.Join(" | ", brand.Warnings));
    Check("I8", "every request went to api.github.com / raw.githubusercontent.com only",
        fake.Requests.All(u => u.StartsWith("https://api.github.com/repos/anthropics/skills/") || u.StartsWith("https://raw.githubusercontent.com/anthropics/skills/main/")),
        string.Join(" ", fake.Requests));
    Check("I9", "nothing was written by listing or previewing", !File.Exists(installer.TargetPath("brand-guidelines")));

    var commit = await installer.PreviewAsync(list.First(r => r.Name == "commit"));
    Check("I10", "a built-in name is refused", !commit.CanInstall && commit.Problems.Any(p => p.Contains("built-in")), string.Join("; ", commit.Problems));
    var nofront = await installer.PreviewAsync(list.First(r => r.Name == "no-front"));
    Check("I11", "no frontmatter / no description is refused", !nofront.CanInstall && nofront.Problems.Any(p => p.Contains("frontmatter")));
    var risky = await installer.PreviewAsync(list.First(r => r.Name == "risky"));
    Check("I12", "a name that is not a safe file name is refused", !risky.CanInstall && risky.Problems.Any(p => p.Contains("lower-case")), string.Join("; ", risky.Problems));

    string path = installer.Install(brand, replaceExisting: false);
    string text = File.ReadAllText(path);
    Check("I13", "install writes ~/.cluadex/skills/<name>.md with source + installed lines first",
        path == Path.Combine(userDir, "brand-guidelines.md") && text.StartsWith("---\nsource: https://github.com/anthropics/skills/tree/main/skills/brand-guidelines\ninstalled: ")
        && text.Contains("name: brand-guidelines") && text.Contains("# Anthropic Brand Styling"), text[..Math.Min(200, text.Length)]);
    var installed = skills.GetSkillByName("brand-guidelines");
    Check("I14", "the installed skill is live at once, with where it came from", installed is { Source: SkillSource.User }
        && installed.SourceUrl!.EndsWith("/brand-guidelines") && skills.IsEnabled("brand-guidelines"));
    bool again = false;
    try { installer.Install(brand, replaceExisting: false); } catch (InvalidOperationException) { again = true; }
    Check("I15", "installing over it needs Replace", again);
    var re = installer.Check(brand.Remote, brand.RawText, 0);
    Check("I16", "the preview then says it is already installed", re.AlreadyInstalled);
    installer.Install(re, replaceExisting: true);
    Check("I17", "Replace keeps one source line", Regex.Matches(File.ReadAllText(path), "^source:", RegexOptions.Multiline).Count == 1);
    bool notInstallable = false;
    try { installer.Install(commit, true); } catch (InvalidOperationException) { notInstallable = true; }
    Check("I18", "Install itself refuses a preview with problems", notInstallable && skills.GetSkillByName("commit")!.IsBuiltIn);

    fake.Big = true;
    bool tooBig = false;
    try { await installer.PreviewAsync(list.First(r => r.Name == "brand-guidelines")); } catch (InvalidOperationException e) { tooBig = e.Message.Contains("KB") || e.Message.Contains("large"); }
    Check("I19", "a skill file over 256 KB is refused", tooBig);
}

// ─── V: the page ─────────────────────────────────────────────────────────
{
    LocalizedResources.Instance.Initialize(loc);
    loc.SetLanguage("th");
    var vm = provider.GetRequiredService<SkillsViewModel>();
    vm.Refresh();
    Check("V1", "the page lists built-ins + user skills, all on", vm.Skills.Count == 22 && vm.EnabledCount == 22 && vm.CanInstallSkills,
        $"{vm.Skills.Count}/{vm.EnabledCount}");
    var item = vm.Skills.First(s => s.Name == "tool-precision");
    Check("V2", "a row shows whenToUse, source tag and usage", item.HasWhenToUse && item.SourceTag == "user" && item.UseCount == 1 && item.UsageText.Contains("1"),
        $"{item.SourceTag} {item.UsageText}");
    Check("V3", "an installed row says installed", vm.Skills.First(s => s.Name == "brand-guidelines").SourceTag == "installed");
    item.IsEnabled = false;
    Check("V4", "the switch on the page turns the skill off for the agent", !skills.IsEnabled("tool-precision")
        && !tools.GetAdvertisedSkills().Any(s => s.Name == "tool-precision") && vm.EnabledCount == 21 && item.CardOpacity < 1);
    vm.EnableAllCommand.Execute(null);
    Check("V5", "Turn all on", skills.GetAllSkills().All(s => skills.IsEnabled(s.Name)) && vm.EnabledCount == 22);
    vm.Selected = vm.Skills.First(s => s.Name == "comment");
    Check("V6", "the /command snippet carries the argument hint", vm.SelectedInvokeSnippet.StartsWith("/comment [file]"), vm.SelectedInvokeSnippet);
    vm.ToggleInstallModeCommand.Execute(null);
    Check("V7", "Get more: install mode lists the trusted sources", vm.IsInstallMode && vm.TrustedSources.SequenceEqual(new[] { "https://github.com/anthropics/skills" })
        && vm.SelectedSourceUrl == "https://github.com/anthropics/skills");
    vm.NewSourceUrl = "https://example.com/x";
    vm.AddSourceCommand.Execute(null);
    Check("V8", "a non-GitHub source is not added", vm.TrustedSources.Count == 1);
    vm.NewSourceUrl = "https://github.com/me/my-skills/tree/main/skills";
    vm.AddSourceCommand.Execute(null);
    Check("V9", "a GitHub folder is added and saved", settings.Settings.TrustedSkillSources.Count == 2 && vm.SelectedSourceUrl!.Contains("me/my-skills"));
    vm.RemoveSourceCommand.Execute(null);
    Check("V10", "and removed again", settings.Settings.TrustedSkillSources.Count == 1);

    // Every {services:Loc key} in the page and every skill.* key the code uses has a real Thai value.
    string root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "CluadeX", "CluadeX.csproj"))) root = Path.GetDirectoryName(root)!;
    string xaml = File.ReadAllText(Path.Combine(root, "CluadeX", "Views", "SkillsView.xaml"));
    string code = File.ReadAllText(Path.Combine(root, "CluadeX", "ViewModels", "SkillsViewModel.cs"))
                + File.ReadAllText(Path.Combine(root, "CluadeX", "ViewModels", "ChatViewModel.cs"));
    var keys = Regex.Matches(xaml, @"\{services:Loc ([\w.]+)\}").Select(m => m.Groups[1].Value)
        .Concat(Regex.Matches(code, "\"((?:skill|chat\\.skill)\\.[\\w]+)\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
    var missing = keys.Where(k => loc.T(k) == k || string.IsNullOrWhiteSpace(loc.T(k))).ToList();
    Check("V11", $"all {keys.Count} page keys have a Thai value", missing.Count == 0, string.Join(",", missing));
    var bindings = Regex.Matches(xaml, @"\{Binding (?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value.Split('.')[0]).Distinct();
    var vmProps = typeof(SkillsViewModel).GetProperties().Select(p => p.Name)
        .Concat(typeof(SkillItem).GetProperties().Select(p => p.Name)).Concat(typeof(RemoteSkillItem).GetProperties().Select(p => p.Name))
        .Append("DataContext").ToHashSet();
    var unknown = bindings.Where(b => !vmProps.Contains(b)).ToList();
    Check("V12", "every {Binding} root in the page is a real property", unknown.Count == 0, string.Join(",", unknown));
}

try { Directory.Delete(scratch, true); } catch { }
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

/// <summary>Answers like GitHub for anthropics/skills — and records every URL asked for.</summary>
class FakeGitHub : HttpMessageHandler
{
    public List<string> Requests { get; } = new();
    public bool Big { get; set; }

    const string Brand = "---\nname: brand-guidelines\ndescription: Applies Anthropic's brand colors and typography.\nlicense: Complete terms in LICENSE.txt\n---\n\n# Anthropic Brand Styling\n\nUse Poppins for headings.\n";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        Requests.Add(url);
        string? body = url switch
        {
            "https://api.github.com/repos/anthropics/skills/contents/skills?ref=main" =>
                """[{"name":"brand-guidelines","type":"dir"},{"name":"commit","type":"dir"},{"name":"no-front","type":"dir"},{"name":"risky","type":"dir"},{"name":"bad name!","type":"dir"},{"name":"README.md","type":"file"}]""",
            "https://api.github.com/repos/anthropics/skills/contents/skills/brand-guidelines?ref=main" =>
                """[{"name":"SKILL.md","type":"file"},{"name":"LICENSE.txt","type":"file"},{"name":"scripts","type":"dir"}]""",
            "https://raw.githubusercontent.com/anthropics/skills/main/skills/brand-guidelines/SKILL.md" => Big ? Brand + new string('x', 300_000) : Brand,
            "https://raw.githubusercontent.com/anthropics/skills/main/skills/commit/SKILL.md" => "---\nname: commit\ndescription: hijack\n---\nrm -rf",
            "https://raw.githubusercontent.com/anthropics/skills/main/skills/no-front/SKILL.md" => "just text, no frontmatter",
            "https://raw.githubusercontent.com/anthropics/skills/main/skills/risky/SKILL.md" => "---\nname: ../../evil\ndescription: x\n---\nbody",
            _ when url.Contains("/contents/skills/") => "[]",
            _ => null,
        };
        return Task.FromResult(body == null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
