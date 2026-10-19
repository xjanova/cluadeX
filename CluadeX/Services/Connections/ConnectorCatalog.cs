namespace CluadeX.Services.Connections;

/// <summary>How a service proves who the user is.</summary>
public enum ConnectorAuth
{
    /// <summary>The GitHub CLI's own browser sign-in (`gh auth login --web`); the token is read from `gh auth token` each start.</summary>
    GhCli,
    /// <summary>A key the user pastes once; kept DPAPI-encrypted on this PC, handed to the server in an environment variable.</summary>
    ApiKey,
    /// <summary>The service's own browser sign-in, done by the server on its first start (OAuth through mcp-remote).</summary>
    Browser,
}

/// <summary>What a connector needs on this PC before it can run.</summary>
public enum ConnectorNeed { None, GhCli, Node }

/// <summary>
/// One service CluadeX can connect the AI to, through that service's OFFICIAL MCP server. Everything here is a
/// constant: no model, page or file decides what is run. The server is never started with a secret on its command
/// line — the launcher (<see cref="ConnectorLauncher"/>) puts it in the child's environment at start.
/// </summary>
/// <param name="Id">Short id: the BrainX bridge id and the CluadeX MCP server name ([a-z0-9-], ≤16, no "__").</param>
/// <param name="Glyph">Segoe MDL2 / Fluent glyph for the row.</param>
/// <param name="KeyEnv">The environment variable the server reads its key from (ApiKey / GhCli).</param>
/// <param name="KeyPage">Where the user gets a key / signs in (https only).</param>
/// <param name="KeyPrefixes">Accepted key prefixes (a typo or a key for another service is refused before it is saved).</param>
/// <param name="Signatures">Text in a server's command / args / url that means "this service is already connected" —
/// to recognise one the user set up by hand, in BrainX or in CluadeX, and never add a second.</param>
/// <param name="ShadowsBuiltInGitHub">While it is connected, CluadeX's own gh-based GitHub tools step aside (same jobs,
/// one set of tools for the model).</param>
public sealed record ConnectorDef(
    string Id,
    string Name,
    string Glyph,
    ConnectorAuth Auth,
    ConnectorNeed Needs,
    string? KeyEnv,
    string KeyPage,
    string Docs,
    IReadOnlyList<string> KeyPrefixes,
    IReadOnlyList<string> Signatures,
    bool ShadowsBuiltInGitHub = false,
    int StartupTimeoutSeconds = 120)
{
    public string TitleKey => "connect.item." + Id;
    public string WhyKey => "connect.why." + Id;
    public string GuideKey => "connect.guide." + Id;
}

/// <summary>The services offered on the Setup page's "Connections" group. Adding one = one entry here, its launch line in
/// <see cref="ConnectorLauncher.Plan"/>, and its texts (title, why, guide).</summary>
public static class ConnectorCatalog
{
    /// <summary>GitHub's official server (github/github-mcp-server) — a single exe from its release, checksum-verified.</summary>
    public const string GitHubServerRepo = "github/github-mcp-server";
    public const string GitHubServerAsset = "github-mcp-server_Windows_x86_64.zip";
    public const string GitHubServerExe = "github-mcp-server.exe";

    /// <summary>Pinned package versions: an update is a reviewed change here, never whatever npm serves today.</summary>
    public const string StripePackage = "@stripe/mcp@0.3.3";
    public const string McpRemotePackage = "mcp-remote@0.15.1";
    public const string CanvaUrl = "https://mcp.canva.com/mcp";

    /// <summary>Canva's setup page asks for Node 22.16 or later (mcp-remote).</summary>
    public static readonly Version CanvaMinNode = new(22, 16);

    public static readonly IReadOnlyList<ConnectorDef> All = new[]
    {
        new ConnectorDef("github", "GitHub", "", ConnectorAuth.GhCli, ConnectorNeed.GhCli,
            KeyEnv: "GITHUB_PERSONAL_ACCESS_TOKEN", KeyPage: "https://github.com/login",
            Docs: "https://github.com/github/github-mcp-server",
            KeyPrefixes: Array.Empty<string>(),
            Signatures: new[] { "github-mcp-server", "api.githubcopilot.com/mcp", "@modelcontextprotocol/server-github" },
            ShadowsBuiltInGitHub: true),
        new ConnectorDef("stripe", "Stripe", "", ConnectorAuth.ApiKey, ConnectorNeed.Node,
            KeyEnv: "STRIPE_SECRET_KEY", KeyPage: "https://dashboard.stripe.com/apikeys",
            Docs: "https://docs.stripe.com/mcp",
            KeyPrefixes: new[] { "rk_test_", "rk_live_", "sk_test_", "sk_live_" },
            Signatures: new[] { "@stripe/mcp", "mcp.stripe.com", "mcp/stripe" }),
        new ConnectorDef("canva", "Canva", "", ConnectorAuth.Browser, ConnectorNeed.Node,
            KeyEnv: null, KeyPage: "https://www.canva.com/login",
            Docs: "https://www.canva.dev/docs/connect/canva-mcp-server-setup",
            KeyPrefixes: Array.Empty<string>(),
            Signatures: new[] { "mcp.canva.com" },
            StartupTimeoutSeconds: 300),   // the first start waits for the browser sign-in
    };

    public static ConnectorDef? Find(string? id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Is this key plausibly one for <paramref name="c"/>? (Prefix and shape only — the service checks it for real.)</summary>
    public static bool LooksLikeKey(ConnectorDef c, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim();
        if (key.Length < 16 || key.Length > 512 || key.Any(char.IsWhiteSpace)) return false;
        return c.KeyPrefixes.Count == 0 || c.KeyPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>A live Stripe key can move real money: the page says so and suggests a restricted (rk_) key.</summary>
    public static bool IsLiveSecretKey(string key) => key.StartsWith("sk_live_", StringComparison.Ordinal);

    /// <summary>Does a server line (command + args, or a url) belong to this service?</summary>
    public static bool Recognises(ConnectorDef c, string? command, IEnumerable<string>? args, string? url = null)
    {
        string line = string.Join(' ', new[] { command ?? "", url ?? "" }.Concat(args ?? Array.Empty<string>()));
        if (line.Contains("--connector", StringComparison.Ordinal) && (args ?? Array.Empty<string>()).Contains(c.Id, StringComparer.OrdinalIgnoreCase))
            return true;   // our own launcher entry
        return c.Signatures.Any(s => line.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"v24.11.1" → 24.11.1 (null when it isn't a version).</summary>
    public static Version? ParseNodeVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V').Split(' ', '\n')[0];
        return Version.TryParse(t, out var v) ? v : null;
    }
}
