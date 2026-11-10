// skilltest — the Skills system end to end, offline, against the REAL CluadeX.dll:
//   P* the frontmatter parser (CluadeX keys and the Anthropic / Claude Code spelling)
//   D* discovery and the cache following the disk (a skill saved seconds ago is found)
//   E* switching a skill off: gone from the prompt list, refused by skill_invoke, counted
//   I* installing from a trusted source through a fake network (no request leaves this PC)
//   V* the Skills page view model and its localisation keys
//   K* what skills cost per turn / when they run, and the gauge's ceiling (Skills/SkillBudget)
//   A* picking the skills that fit a turn: English, Thai, named, pinned, learned, the brain's ranking (Skills/SkillPicker)
//   L* words learned from turns that ran a skill, and pins (SkillService)
//   G* the composer's per-chat switch (Skills/SkillPickGate)
//   B* the brain's skill cards and its ranking answer (Skills/SkillBrainIndex — no brain on this PC)
//   S* the system prompt is the same whatever a turn picked (the prompt cache holds)
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
                + File.ReadAllText(Path.Combine(root, "CluadeX", "ViewModels", "ChatViewModel.cs"))
                + File.ReadAllText(Path.Combine(root, "CluadeX", "ViewModels", "ChatViewModel.SkillPick.cs"));
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

// ─── K: what skills cost (SkillBudget) ───────────────────────────────────
{
    var all = skills.GetAllSkills();
    var scan = all.First(s => s.Name == "security-scan");
    int line = CluadeX.Services.Skills.SkillBudget.ListingTokens(scan, false);
    Check("K1", "a skill's per-turn cost is its listing line, small; its load cost is its whole prompt, larger",
        line > 5 && line < 80 && CluadeX.Services.Skills.SkillBudget.LoadTokens(scan) > line * 3, $"{line} / {CluadeX.Services.Skills.SkillBudget.LoadTokens(scan)}");
    Check("K2", "the ceiling is 1% of the window, 250..4000",
        CluadeX.Services.Skills.SkillBudget.CapFor(200_000) == 2000 && CluadeX.Services.Skills.SkillBudget.CapFor(8192) == 250
        && CluadeX.Services.Skills.SkillBudget.CapFor(1_000_000) == 4000 && CluadeX.Services.Skills.SkillBudget.CapFor(0) == 250);
    var g = CluadeX.Services.Skills.SkillBudget.Measure(all, false, 200_000);
    Check("K3", "the gauge measures the header + every line, the ceiling sits at half the bar",
        g.Used == CluadeX.Services.Skills.SkillBudget.HeaderTokens + all.Sum(s => CluadeX.Services.Skills.SkillBudget.ListingTokens(s, false))
        && Math.Abs(g.Fill - Math.Min(1, g.Used / 4000.0)) < 1e-9 && g.Count == all.Count, $"{g.Used} {g.Fill}");
    var small = CluadeX.Services.Skills.SkillBudget.Measure(all, false, 8192);
    Check("K4", "the same list on a small window is over the ceiling; nothing on = Empty",
        small.Level == CluadeX.Services.Skills.SkillBudget.Level.Over
        && CluadeX.Services.Skills.SkillBudget.Measure(Array.Empty<SkillDefinition>(), false, 8192).Level == CluadeX.Services.Skills.SkillBudget.Level.Empty
        && CluadeX.Services.Skills.SkillBudget.Measure(new[] { scan }, false, 200_000).Level == CluadeX.Services.Skills.SkillBudget.Level.Light,
        small.Level.ToString());
    Check("K5", "Thai costs more per character than English (the listing word ใช้เมื่อ counts)",
        CluadeX.Services.Skills.SkillBudget.ListingTokens(all.First(s => s.Name == "tool-precision"), true)
        > CluadeX.Services.Skills.SkillBudget.ListingTokens(all.First(s => s.Name == "tool-precision"), false));
    var heavy = new SkillDefinition { Name = "big", Description = "x", PromptContent = new string('a', 4000 * 4) };
    Check("K6", "a skill whose prompt takes over a tenth of the window is heavy", CluadeX.Services.Skills.SkillBudget.HeavyToLoad(heavy, 32_000)
        && !CluadeX.Services.Skills.SkillBudget.HeavyToLoad(heavy, 200_000));
}

// ─── A: the picker (SkillPicker) ─────────────────────────────────────────
{
    var all = skills.GetAllSkills();
    CluadeX.Services.Skills.SkillPick P(string msg, string brain = null, IReadOnlyList<string> ranked = null, string[] pinned = null,
        Func<string, IReadOnlyDictionary<string, int>> learned = null, int budget = 2000, int max = 5) =>
        CluadeX.Services.Skills.SkillPicker.Pick(new(msg, brain, all, pinned ?? Array.Empty<string>(), learned, budget, max, false, ranked));
    string Names(CluadeX.Services.Skills.SkillPick p) => string.Join(",", p.Names);

    var p1 = P("scan this code for security holes and hardcoded secrets");
    Check("A1", "an English security task picks security-scan first", p1.Picked.FirstOrDefault()?.Skill.Name == "security-scan", Names(p1));
    Check("A1b", "…and not a skill sharing one weak word with it (market-research: \"Competitive scan\")", !p1.Names.Contains("market-research"), Names(p1));
    var p2 = P("ช่วยตรวจช่องโหว่ความปลอดภัยของโค้ดนี้หน่อย");
    Check("A2", "the same task in Thai picks security-scan (Thai → English words)", p2.Names.Contains("security-scan"), Names(p2));
    var p3 = P("เขียนเทสต์ end-to-end แล้วรันทดสอบให้หน่อย");
    Check("A3", "a Thai testing task picks e2e-testing", p3.Names.Contains("e2e-testing"), Names(p3));
    var p4 = P("hi");
    Check("A4", "\"hi\" picks nothing, and the block is empty", p4.Picked.Count == 0 && p4.BlockFor(true) == "" && p4.TokensPicked == 0, Names(p4));
    var p5 = P("ช่วย commit งานนี้ให้หน่อย");
    Check("A5", "a skill named in the message comes first (commit)", p5.Picked.FirstOrDefault() is { Reason: CluadeX.Services.Skills.PickReason.Named } n && n.Skill.Name == "commit", Names(p5));
    Check("A5", "/name names it too; a bare short word inside another word does not",
        CluadeX.Services.Skills.SkillPicker.IsNamed("simplify", "/simplify now") && CluadeX.Services.Skills.SkillPicker.IsNamed("review-pr", "do a review-pr pass")
        && !CluadeX.Services.Skills.SkillPicker.IsNamed("commit", "recommitted") && !CluadeX.Services.Skills.SkillPicker.IsNamed("comment", "commented"));
    var p6 = P("hi", pinned: new[] { "tool-precision" });
    Check("A6", "a pinned skill is offered on every turn", p6.Picked.Count == 1 && p6.Picked[0].Reason == CluadeX.Services.Skills.PickReason.Pinned, Names(p6));
    var many = P("security test review simplify research cost context content market plan comment eval debug loop agent", max: 3);
    Check("A7", "a pick never offers more than its maximum", many.Picked.Count <= 3, Names(many));
    var tight = P("security test review simplify research cost context content market plan comment eval", budget: 120);
    Check("A8", "…nor more tokens than its budget", tight.TokensPicked <= 120 || tight.Picked.All(e => e.Reason is CluadeX.Services.Skills.PickReason.Named or CluadeX.Services.Skills.PickReason.Pinned),
        $"{tight.TokensPicked} {Names(tight)}");
    Check("A9", "the pick costs less than the full list", p1.TokensPicked < p1.TokensAll && p1.Offered == all.Count, $"{p1.TokensPicked}/{p1.TokensAll}");
    var learnedMap = new Dictionary<string, int> { ["checkout"] = 3, [CluadeX.Services.Skills.SkillPicker.Stem("staging")] = 2 };
    var p10 = P("checkout is broken on staging", learned: name => name == "e2e-testing" ? learnedMap : null);
    Check("A10", "words learned for a skill pick it for a task said that way", p10.Names.Contains("e2e-testing")
        && p10.Picked.First(e => e.Skill.Name == "e2e-testing").Reason == CluadeX.Services.Skills.PickReason.Learned, Names(p10));
    var one = new Dictionary<string, int> { ["checkout"] = 3, ["keep"] = 3 };
    var p10b = P("keep the readme short", learned: name => name == "e2e-testing" ? one : null);
    Check("A10b", "one learned word in common is a coincidence, not a pick", !p10b.Names.Contains("e2e-testing"), Names(p10b));
    var p11 = P("make it better please", ranked: new[] { "harness-optimizer", "multi-plan" });
    Check("A11", "the brain's first-ranked card is picked on its own; its later ones need a word of the task",
        p11.Names.Contains("harness-optimizer") && !p11.Names.Contains("multi-plan")
        && p11.Picked.First(e => e.Skill.Name == "harness-optimizer").Reason == CluadeX.Services.Skills.PickReason.Brain, Names(p11));
    var p12 = P("make it better please", brain: "Notes: the OWASP security audit found hardcoded secrets and missing rate limits");
    Check("A12", "the brain's recall text alone is a weak signal (it never outweighs the task's own words)",
        !p12.Names.Contains("market-research") && p12.Picked.All(e => e.Score < 100), Names(p12));
    string block = p1.BlockFor(true);
    Check("A13", "the block names each picked skill with the prompt's own line and says how to list the rest",
        block.StartsWith("[Skill ที่เข้ากับงานนี้") && block.Contains(CluadeX.Services.Skills.SkillBudget.ListingLine(p1.Picked[0].Skill, true)) && block.Contains("skill_invoke"));
    Check("A14", "Terms: stems, stop words, Thai", CluadeX.Services.Skills.SkillPicker.Terms("Testing the tests reviewed").SequenceEqual(new[] { "test", "review" })
        && CluadeX.Services.Skills.SkillPicker.Terms("ช่วยทดสอบ").Contains("test") && CluadeX.Services.Skills.SkillPicker.Terms("").Count == 0,
        string.Join(",", CluadeX.Services.Skills.SkillPicker.Terms("Testing the tests reviewed")));
    var huge = new string('x', 200_000) + " security";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    P(huge);
    Check("A15", "a huge message is cut, the pick stays fast", sw.ElapsedMilliseconds < 1500, sw.ElapsedMilliseconds + " ms");
}

// ─── L: learned words and pins (SkillService) ────────────────────────────
{
    skills.LearnTerms("verification-loop", "the payment webhook retries forever");
    skills.LearnTerms("verification-loop", "webhook retries again");
    var t = skills.LearnedTerms("verification-loop");
    Check("L1", "the words of a turn that ran a skill are counted for it", t.TryGetValue("webhook", out var c) && c == 2 && t.ContainsKey("payment"), string.Join(",", t.Keys));
    for (int i = 0; i < 60; i++) skills.LearnTerms("verification-loop", $"word{i:00}a word{i:00}b");
    t = skills.LearnedTerms("verification-loop");
    Check("L2", "at most 40 words are kept, the most counted stay", t.Count == SkillService.MaxLearnedTerms && t.ContainsKey("webhook"), t.Count.ToString());
    string usageFile = Path.Combine(settings.DataRoot, "skill-usage.json");
    Check("L3", "the words live in skill-usage.json (inside the harness's data root)", File.Exists(usageFile) && File.ReadAllText(usageFile).Contains("webhook"));
    skills.LearnTerms("verification-loop", null);
    skills.LearnTerms("", "x y z");
    Check("L4", "nothing to learn is not an error", true);
    skills.SetPinned("simplify", true);
    Check("L5", "pinning saves to settings and survives a re-read", skills.IsPinned("simplify") && settings.Settings.PinnedSkills.Contains("simplify"));
    skills.SetPinned("simplify", false);
    Check("L6", "and unpins", !skills.IsPinned("simplify") && settings.Settings.PinnedSkills.Count == 0);
}

// ─── G: the per-chat switch (SkillPickGate) ──────────────────────────────
{
    CluadeX.Services.Skills.SkillPickGate.Load(settings.DataRoot);
    Check("G1", "a chat with no override follows the default; no chat = off",
        CluadeX.Services.Skills.SkillPickGate.IsOn("c1", true) && !CluadeX.Services.Skills.SkillPickGate.IsOn("c1", false)
        && !CluadeX.Services.Skills.SkillPickGate.IsOn(null, true));
    CluadeX.Services.Skills.SkillPickGate.Set("c1", false, defaultOn: true);
    CluadeX.Services.Skills.SkillPickGate.Load(settings.DataRoot);
    Check("G2", "a chat switched off stays off after a restart, others still follow the default",
        !CluadeX.Services.Skills.SkillPickGate.IsOn("c1", true) && CluadeX.Services.Skills.SkillPickGate.IsOn("c2", true));
    CluadeX.Services.Skills.SkillPickGate.Set("c1", true, defaultOn: true);
    string f = Path.Combine(settings.DataRoot, "skill-pick-chats.json");
    Check("G3", "switched back to the default = no entry kept", File.Exists(f) && !File.ReadAllText(f).Contains("c1"), File.Exists(f) ? File.ReadAllText(f) : "-");
    CluadeX.Services.Skills.SkillPickGate.Set("c3", false, true);
    CluadeX.Services.Skills.SkillPickGate.Forget("c3");
    Check("G4", "a deleted chat's override goes", !File.ReadAllText(f).Contains("c3"));
    File.WriteAllText(f, "{not json");
    CluadeX.Services.Skills.SkillPickGate.Load(settings.DataRoot);
    Check("G5", "a damaged file = every chat follows the default", CluadeX.Services.Skills.SkillPickGate.IsOn("c1", true));
}

// ─── B: the brain's cards (SkillBrainIndex, no brain on this PC) ─────────
{
    var scan = skills.GetAllSkills().First(s => s.Name == "security-scan");
    string body = CluadeX.Services.Skills.SkillBrainIndex.CardBody(scan);
    Check("B1", "a card is the name, description and slash command — never the prompt",
        CluadeX.Services.Skills.SkillBrainIndex.CardTitle(scan) == "CluadeX skill: security-scan" && body.Contains(scan.Description)
        && !body.Contains("OWASP Top 10") && body.Contains("/security-scan"));
    var copy = SkillService.ParseSkillContent("---\nname: security-scan\ndescription: other text\n---\nbody", "x");
    Check("B2", "the card's hash follows its text", CluadeX.Services.Skills.SkillBrainIndex.CardHash(scan) != CluadeX.Services.Skills.SkillBrainIndex.CardHash(copy)
        && CluadeX.Services.Skills.SkillBrainIndex.CardHash(scan) == CluadeX.Services.Skills.SkillBrainIndex.CardHash(scan));
    var ranked = CluadeX.Services.Skills.SkillBrainIndex.ParseRanked("""
        {"query":"x","results":[{"id":"1","title":"CluadeX skill: e2e-testing"},{"id":"2","title":"Some other note"},
        {"id":"3","title":"CluadeX skill: ../evil"},{"id":"4","title":"CluadeX skill: security-scan"},{"id":"5","title":"CluadeX skill: e2e-testing"}]}
        """);
    Check("B3", "the brain's answer → skill names, best first; other notes, bad names and repeats dropped",
        ranked.SequenceEqual(new[] { "e2e-testing", "security-scan" }), string.Join(",", ranked));
    Check("B4", "an answer that is not JSON is an empty ranking", CluadeX.Services.Skills.SkillBrainIndex.ParseRanked("(brain search error: timeout)").Count == 0
        && CluadeX.Services.Skills.SkillBrainIndex.ParseRanked(null).Count == 0);
    Check("B4b", "a card already in the brain is found by its exact title (not written twice after a reinstall)",
        CluadeX.Services.Skills.SkillBrainIndex.IdForTitle("""{"results":[{"id":"a","title":"CluadeX skill: security-scan v2"},{"id":"b","title":"CluadeX skill: security-scan"}]}""",
            "CluadeX skill: security-scan") == "b"
        && CluadeX.Services.Skills.SkillBrainIndex.IdForTitle("""{"results":[]}""", "CluadeX skill: x") == null
        && CluadeX.Services.Skills.SkillBrainIndex.IdForTitle("oops", "x") == null);
    var index = provider.GetRequiredService<CluadeX.Services.Skills.SkillBrainIndex>();
    var r = index.RankAsync("scan for security holes").GetAwaiter().GetResult();
    int w = index.EnsureIndexedAsync(skills.GetAllSkills()).GetAwaiter().GetResult();
    Check("B5", "with no brain connected: no ranking, nothing written, nothing thrown", r.Count == 0 && w == 0 && !index.BrainReady);
    Check("B6", "every skill's card is missing until a brain takes it", index.Stale(skills.GetAllSkills()).Count == skills.GetAllSkills().Count);
}

// ─── S: the system prompt stays the same whatever a turn picks ───────────
{
    var agent = provider.GetRequiredService<CodeAgentService>();
    var wasProvider = settings.Settings.ActiveProvider;
    settings.Settings.ActiveProvider = AiProviderType.Anthropic;
    try
    {
        string plain = agent.GetSystemPrompt();
        var scope = new AgentRunScope("chat-s", null);
        AgentRunScope.Enter(scope);
        scope.SkillPick = CluadeX.Services.Skills.SkillPicker.Pick(new("scan for security holes", null, tools.GetAdvertisedSkills(),
            Array.Empty<string>(), null, 2000, 5, false));
        string a = agent.GetSystemPrompt();
        scope.SkillPick = CluadeX.Services.Skills.SkillPicker.Pick(new("write a blog post", null, tools.GetAdvertisedSkills(),
            Array.Empty<string>(), null, 2000, 5, false));
        string b = agent.GetSystemPrompt();
        AgentRunScope.Enter(null);
        Check("S1", "without picking, the system prompt lists every offered skill with SkillBudget's line",
            tools.GetAdvertisedSkills().All(s => plain.Contains(CluadeX.Services.Skills.SkillBudget.ListingLine(s, loc.CurrentLanguage == "th"))));
        Check("S2", "with picking, it names no skill — and two turns that picked differently send the same system prompt",
            a == b && !a.Contains("`security-scan`:") && !a.Contains("`content-engine`:") && a.Contains("skill_invoke"),
            a == b ? "" : "the prompts differ");
    }
    finally { settings.Settings.ActiveProvider = wasProvider; AgentRunScope.Enter(null); }
}

// ─── V13: every smart-picking key has a Thai value ───────────────────────
{
    var t = typeof(CluadeX.App).Assembly.GetType("CluadeX.Services.Localization.SkillPickKeys")!;
    var dict = (Dictionary<string, Dictionary<string, string>>)t.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var bad = dict.Where(kv => !kv.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th) || !kv.Value.ContainsKey("en")
        || loc.T(kv.Key) == kv.Key).Select(kv => kv.Key).ToList();
    Check("V13", $"all {dict.Count} smart-picking keys have English and Thai, and are loaded", bad.Count == 0, string.Join(",", bad));
    var why = Enum.GetNames<CluadeX.Services.Skills.PickReason>().Select(n => "skill.why." + n.ToLowerInvariant()).Where(k => loc.T(k) == k).ToList();
    Check("V14", "every pick reason has words", why.Count == 0, string.Join(",", why));
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
