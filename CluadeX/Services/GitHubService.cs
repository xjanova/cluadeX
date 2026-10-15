using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services;

/// <summary>
/// GitHub API integration - clone repos, browse public repos, check PRs.
/// Uses either `gh` CLI (if installed) or direct HTTP API for public endpoints.
/// </summary>
public class GitHubService
{
    private readonly GitService _gitService;
    private readonly SettingsService _settingsService;
    private static readonly HttpClient _http = new();

    public GitHubService(GitService gitService, SettingsService settingsService)
    {
        _gitService = gitService;
        _settingsService = settingsService;
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("CluadeX-AI-Assistant/1.0");
    }

    // ═══════════════════════════════════════════
    // Clone Repository
    // ═══════════════════════════════════════════

    /// <summary>Clone a GitHub repository. Accepts full URL or owner/repo format.</summary>
    public async Task<GitResult> CloneRepoAsync(string repoInput, string? targetDir = null, CancellationToken ct = default)
    {
        // Normalize input
        string repoUrl = NormalizeRepoUrl(repoInput);

        // Determine target directory: the caller's, else <Projects>\<repo name>. The name comes from the URL
        // (which the model wrote), so it is reduced to one safe folder name and the result must lie inside
        // Projects — "…/C:foo" used to make Path.Combine drop the Projects folder altogether.
        string? target = targetDir ?? DefaultCloneTarget(
            _settingsService.Settings.ModelDirectory.Replace("Models", "Projects"), repoUrl);
        if (target == null)
            return new GitResult
            {
                Success = false,
                Error = string.Format(LocalizedResources.Instance["chat.clone.badTarget"], ExtractRepoName(repoUrl)),
            };

        if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0)
        {
            return new GitResult
            {
                Success = false,
                Error = $"Directory already exists and is not empty: {target}",
            };
        }

        return await _gitService.CloneAsync(repoUrl, target, ct);
    }

    // ═══════════════════════════════════════════
    // GitHub CLI (gh) Integration
    // ═══════════════════════════════════════════

    /// <summary>Check if GitHub CLI (gh) is installed.</summary>
    public async Task<bool> IsGhCliInstalledAsync()
    {
        try
        {
            var result = await RunGhAsync("--version");
            return result.Success;
        }
        catch { return false; }
    }

    /// <summary>Check if user is authenticated with gh CLI.</summary>
    public async Task<GhAuthStatus> GetAuthStatusAsync()
    {
        var result = await RunGhAsync("auth status");
        return new GhAuthStatus
        {
            IsAuthenticated = result.Success || result.FullOutput.Contains("Logged in"),
            Output = result.FullOutput,
        };
    }

    /// <summary>Create a Pull Request using gh CLI.</summary>
    public async Task<GitResult> CreatePullRequestAsync(string title, string body, string baseBranch = "main")
    {
        string safeTitle = SanitizeShellArg(title);
        string safeBody = SanitizeShellArg(body);
        string safeBranch = SanitizeShellArg(baseBranch);
        return await RunGhAsync($"pr create --title \"{safeTitle}\" --body \"{safeBody}\" --base {safeBranch}");
    }

    /// <summary>List Pull Requests.</summary>
    public async Task<GitResult> ListPullRequestsAsync(string state = "open", int limit = 10)
    {
        return await RunGhAsync($"pr list --state {state} --limit {limit}");
    }

    /// <summary>View a specific Pull Request.</summary>
    public async Task<GitResult> ViewPullRequestAsync(string prNumber)
    {
        return await RunGhAsync($"pr view {prNumber}");
    }

    /// <summary>List Issues.</summary>
    public async Task<GitResult> ListIssuesAsync(string state = "open", int limit = 10)
    {
        return await RunGhAsync($"issue list --state {state} --limit {limit}");
    }

    /// <summary>Create an Issue.</summary>
    public async Task<GitResult> CreateIssueAsync(string title, string body)
    {
        string safeTitle = SanitizeShellArg(title);
        string safeBody = SanitizeShellArg(body);
        return await RunGhAsync($"issue create --title \"{safeTitle}\" --body \"{safeBody}\"");
    }

    /// <summary>View repo info.</summary>
    /// <summary>View a repo. Empty <paramref name="repo"/> = the repo in the working directory;
    /// otherwise "owner/name" (validated, since it is interpolated into the gh command line).</summary>
    public async Task<GitResult> ViewRepoAsync(string repo = "")
    {
        repo = (repo ?? "").Trim();
        if (repo.Length == 0) return await RunGhAsync("repo view");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$"))
            return new GitResult { Success = false, Error = "Invalid repo — expected owner/name." };
        return await RunGhAsync($"repo view {repo}");
    }

    /// <summary>List releases.</summary>
    public async Task<GitResult> ListReleasesAsync(int limit = 5)
    {
        return await RunGhAsync($"release list --limit {limit}");
    }

    /// <summary>View workflow runs (CI/CD).</summary>
    public async Task<GitResult> ListWorkflowRunsAsync(int limit = 5)
    {
        return await RunGhAsync($"run list --limit {limit}");
    }

    // ═══════════════════════════════════════════
    // Public GitHub API (no auth needed)
    // ═══════════════════════════════════════════

    /// <summary>Search public repositories on GitHub.</summary>
    public async Task<List<GitHubRepo>> SearchReposAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&sort=stars&per_page={limit}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return new();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GitHubSearchResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return result?.Items ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Get repository info by owner/repo.</summary>
    public async Task<GitHubRepo?> GetRepoInfoAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<GitHubRepo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    /// <summary>
    /// The URL a clone of <paramref name="input"/> really fetches: a full URL as given, "owner/repo" as a
    /// GitHub https URL. Internal so the permission gate judges the same URL the clone uses — matched on
    /// the raw input, "HTTPS://host/x" passed a rule and was then cloned as https://github.com/HTTPS://….
    /// </summary>
    internal static string NormalizeRepoUrl(string input)
    {
        input = (input ?? "").Trim();

        // Already a full URL (scheme names are case-insensitive). http:// is left alone, never rewritten.
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            return input;

        // owner/repo format → https URL
        if (input.Contains('/') && !input.Contains(' '))
            return $"https://github.com/{input}.git";

        return input;
    }

    /// <summary>Strip shell-dangerous characters from arguments.</summary>
    private static string SanitizeShellArg(string input)
        => input.Replace("\"", "'").Replace("`", "").Replace("$", "")
                .Replace(";", "").Replace("&", "").Replace("|", "")
                .Replace("%", "").Replace("^", "").Replace("!", "");

    /// <summary>
    /// Where a clone with no folder given lands: <paramref name="projectsRoot"/>\&lt;repo name&gt; — or null when
    /// that is not strictly inside <paramref name="projectsRoot"/> (it always is for a name from
    /// <see cref="ExtractRepoName"/>; this is the check that proves it).
    /// </summary>
    internal static string? DefaultCloneTarget(string projectsRoot, string repoUrl)
    {
        try
        {
            string root = Path.GetFullPath(projectsRoot);
            string target = Path.GetFullPath(Path.Combine(root, ExtractRepoName(repoUrl)));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return target.Length > prefix.Length && target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? target : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The folder name for a clone of <paramref name="repoUrl"/>, as git names it (the last segment, ".git"
    /// dropped) — reduced to ONE safe file name: the last segment again after URL-decoding ("%2e%2e",
    /// "..%2F"), ':' treated as a separator ("C:foo" is a drive-relative path, which Path.Combine lets replace
    /// the base folder), invalid characters replaced, trailing dots and spaces dropped (".." is the parent).
    /// Nothing usable left, or a device name (CON, NUL, …) → "repo".
    /// </summary>
    internal static string ExtractRepoName(string repoUrl)
    {
        string s = (repoUrl ?? "").Trim();
        if (s.Contains("://"))
        {
            int q = s.IndexOfAny(['?', '#']);   // a URL's query / fragment is not part of the name
            if (q >= 0) s = s[..q];
        }
        string name = LastSegment(s);
        try { name = Uri.UnescapeDataString(name); } catch { }
        name = LastSegment(name);
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        name = name.Trim().TrimEnd('.', ' ');
        return name.Length == 0 || IsDeviceName(name) ? "repo" : name;

        static string LastSegment(string p)
        {
            p = p.TrimEnd('/', '\\');
            int cut = p.LastIndexOfAny(['/', '\\', ':']);
            return cut >= 0 ? p[(cut + 1)..] : p;
        }
        static bool IsDeviceName(string n)
        {
            string stem = n.Split('.')[0].TrimEnd().ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
        }
    }

    private async Task<GitResult> RunGhAsync(string arguments, int timeoutMs = 30000)
    {
        var result = new GitResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                WorkingDirectory = _gitService.WorkingDirectory.Length > 0
                    ? _gitService.WorkingDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Error = "Failed to start gh CLI";
                return result;
            }

            using var cts = new CancellationTokenSource(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token);
                result.Output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                result.Error = await process.StandardError.ReadToEndAsync(cts.Token);
                result.ExitCode = process.ExitCode;
                result.Success = process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                result.Error = "gh command timed out.";
                result.TimedOut = true;
            }
        }
        catch (Exception ex)
        {
            result.Error = $"gh CLI error: {ex.Message}";
        }

        return result;
    }
}

// ─── GitHub API Models ───

public class GhAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string Output { get; set; } = "";
}

public class GitHubSearchResult
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("items")]
    public List<GitHubRepo> Items { get; set; } = new();
}

public class GitHubRepo
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("clone_url")]
    public string CloneUrl { get; set; } = "";

    [JsonPropertyName("stargazers_count")]
    public int Stars { get; set; }

    [JsonPropertyName("forks_count")]
    public int Forks { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }
}
