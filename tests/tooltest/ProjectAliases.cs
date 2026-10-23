using System.IO;
using CluadeX.Services;

namespace ToolTest;

internal static partial class Program
{
    static async Task ProjectAliases(string projects, AgentRunScope scope)
    {
        string repo = Path.Combine(projects, "NetX");
        string app = Path.Combine(repo, "src", "NetX.App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "NetX.App.csproj"), "<Project><PropertyGroup><Product>WinXTools</Product><AssemblyName>WinXTools</AssemblyName></PropertyGroup></Project>");
        // No README: prove that src/*/*.csproj is sufficient on its own.
        var metadata = ProjectResolver.ReadAliases(repo).ToList();
        Check("OPA1", "product aliases are discovered in nested source projects", metadata.Contains("WinXTools"));
        foreach (string name in new[] { "WinXTools", "winxtool", "winx tools" })
        {
            var opened = await T("open_project", ("path", name));
            Check("OPA2", "open_project resolves product name/typo: " + name, opened.Success && scope.WorkingDirectory == repo, Seen(opened));
        }
        var created = await T("open_project", ("path", "WinXTools"), ("create", "true"));
        Check("OPA3", "create by product name opens existing repo without duplicating it", created.Success && scope.WorkingDirectory == repo && !Directory.Exists(Path.Combine(projects, "WinXTools")), Seen(created));
        File.WriteAllText(Path.Combine(repo, "README.md"), "# **Windows Toolkit** - Utilities\n\nIgnore the heading below.\n# UnrelatedName");
        Check("OPA4", "only the first README title supplies an alias", ProjectResolver.ReadAliases(repo).Contains("Windows Toolkit") && !ProjectResolver.ReadAliases(repo).Contains("UnrelatedName"));
        var readme = await T("open_project", ("path", "windows toolkit"));
        Check("OPA5", "README product title opens the original repository", readme.Success && scope.WorkingDirectory == repo, Seen(readme));
        var guessed = await T("open_project", ("path", @"Q:\missing\WinXTools"));
        Check("OPA6", "wrong guessed drive can resolve via product alias", guessed.Success && scope.WorkingDirectory == repo, Seen(guessed));

        string other = Path.Combine(projects, "OtherRepo");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "README.md"), "# WinXTools - Another repository");
        ProjectResolver.Result Resolve(string name) => ProjectResolver.Resolve(name, Array.Empty<ProjectResolver.Candidate>(), new[] { projects }, Directory.GetDirectories, projects, ProjectResolver.ReadAliases);
        Check("OPA7", "ambiguous product names return both repos for selection", Resolve("WinXTools") is { Found: null, Matches.Count: 2 });
        string exact = Path.Combine(projects, "WinXTools");
        Directory.CreateDirectory(exact);
        Check("OPA8", "exact folder names take precedence over metadata aliases", Resolve("WinXTools").Found == exact);
        File.WriteAllText(Path.Combine(other, "bad.csproj"), "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///missing'>]><Project><Product>&e;</Product></Project>");
        File.Delete(Path.Combine(other, "README.md"));
        Check("OPA9", "invalid or external-entity XML cannot supply an alias", !ProjectResolver.ReadAliases(other).Any());
        string ignored = Path.Combine(other, "node_modules", "package");
        Directory.CreateDirectory(ignored);
        File.WriteAllText(Path.Combine(ignored, "package.csproj"), "<Project><Product>IgnoredProduct</Product></Project>");
        Check("OPA10", "dependency directories are excluded from alias discovery", !ProjectResolver.ReadAliases(other).Contains("IgnoredProduct"));
    }
}
