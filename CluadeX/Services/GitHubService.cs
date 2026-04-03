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
        string repoName = ExtractRepoName(repoUrl);

        // Determine target directory
        string target = targetDir ?? Path.Combine(
            _settingsService.Settings.ModelDirectory.Replace("Models", "Projects"), repoName);

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
        string escapedTitle = title.Replace("\"", "\\\"");
        string escapedBody = body.Replace("\"", "\\\"");
        return await RunGhAsync($"pr create --title \"{escapedTitle}\" --body \"{escapedBody}\" --base {baseBranch}");
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
        string escapedTitle = title.Replace("\"", "\\\"");
        string escapedBody = body.Replace("\"", "\\\"");
        return await RunGhAsync($"issue create --title \"{escapedTitle}\" --body \"{escapedBody}\"");
    }

    /// <summary>View repo info.</summary>
    public async Task<GitResult> ViewRepoAsync()
    {
        return await RunGhAsync("repo view");
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

    private static string NormalizeRepoUrl(string input)
    {
        input = input.Trim();

        // Already a full URL
        if (input.StartsWith("https://") || input.StartsWith("git@"))
            return input;

        // owner/repo format → https URL
        if (input.Contains('/') && !input.Contains(' '))
            return $"https://github.com/{input}.git";

        return input;
    }

    private static string ExtractRepoName(string repoUrl)
    {
        string name = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));
        if (string.IsNullOrEmpty(name)) name = "repo";
        return name;
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
