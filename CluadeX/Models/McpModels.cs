using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Models;

// ════════════════════════════════════════════
// MCP Server Configuration
// ════════════════════════════════════════════

/// <summary>Root config file structure (matches Claude Desktop format).</summary>
public class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
}

/// <summary>Configuration for a single MCP server.</summary>
public class McpServerConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Set when CluadeX itself owns this entry (e.g. "cluadex-chrome" for the Claude in Chrome connector): its
    /// command/args are re-resolved by an <see cref="CluadeX.Services.Mcp.IMcpLaunchResolver"/> before every
    /// start and the UI shows it as managed. Null for every server the user added. Omitted from the file when null.
    /// </summary>
    [JsonPropertyName("managedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManagedBy { get; set; }

    /// <summary>How long the initialize handshake may take, in seconds (null = the manager's default). Slow first
    /// starts (npx/uvx downloading a package, a CLI that signs in) need more than the default. Omitted when null;
    /// "30" written as a string by hand is read as 30.</summary>
    [JsonPropertyName("startupTimeoutSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? StartupTimeoutSeconds { get; set; }

    /// <summary>Runtime: server name (key from config).</summary>
    [JsonIgnore]
    public string Name { get; set; } = "";
}

// ════════════════════════════════════════════
// MCP Tool Definitions
// ════════════════════════════════════════════

/// <summary>A tool discovered from an MCP server.</summary>
public class McpTool
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string Description { get; set; } = "";
    public JsonElement? InputSchema { get; set; }

    /// <summary>Which MCP server provides this tool.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Fully qualified name: mcp__{server}__{tool}</summary>
    public string QualifiedName => $"mcp__{ServerName}__{Name}";
}

/// <summary>Content item in a tool result.</summary>
public class McpContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text"; // text, image, audio, resource, resource_link

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; } // base64 for images

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>type "resource": the embedded resource (text or base64 blob).</summary>
    [JsonPropertyName("resource")]
    public McpEmbeddedResource? Resource { get; set; }
}

/// <summary>The body of an MCP "resource" content item: text, or a base64 blob, for one URI.</summary>
public class McpEmbeddedResource
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("blob")]
    public string? Blob { get; set; }
}

/// <summary>Result from tools/call.</summary>
public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContentItem> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    /// <summary>
    /// Flatten the text the model should read into one string: every text item (exactly as before), plus the
    /// text of embedded "resource" items under a "[resource &lt;uri&gt;]" line and a one-line mention of each
    /// "resource_link" — those used to vanish without a trace. Images are NOT here: see <see cref="GetImageItems"/>.
    /// </summary>
    public string GetTextOutput()
    {
        var texts = new List<string>();
        foreach (var c in Content)
        {
            if (c == null) continue;
            switch (c.Type)
            {
                case "text" when c.Text != null:
                    texts.Add(c.Text);
                    break;
                case "resource" when c.Resource?.Text != null:
                    texts.Add($"[resource {c.Resource.Uri ?? "(no uri)"}]\n{c.Resource.Text}");
                    break;
                case "resource_link" when !string.IsNullOrEmpty(c.Uri):
                    texts.Add($"[resource link: {(string.IsNullOrEmpty(c.Name) ? "" : c.Name + " ")}{c.Uri}]");
                    break;
            }
        }
        return string.Join("\n", texts);
    }

    /// <summary>
    /// The image items, in order: every type "image" item with data, plus embedded "resource" blobs whose
    /// mimeType is image/* (returned as an image item). The base64 is untouched — decoding, size caps and
    /// downscaling belong to the caller (Vision.ImageCodec).
    /// </summary>
    public List<McpContentItem> GetImageItems()
    {
        var images = new List<McpContentItem>();
        foreach (var c in Content)
        {
            if (c == null) continue;
            if (c.Type == "image" && !string.IsNullOrEmpty(c.Data))
                images.Add(c);
            else if (c.Type == "resource" && c.Resource is { Blob: { Length: > 0 } blob } r
                     && (r.MimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                images.Add(new McpContentItem { Type = "image", Data = blob, MimeType = r.MimeType, Uri = r.Uri });
        }
        return images;
    }

    /// <summary>Items the model cannot be given (audio, a non-image binary resource): type and mime, so the
    /// result can say what was left out instead of dropping it silently.</summary>
    public List<string> GetUnsupportedItemKinds()
    {
        var kinds = new List<string>();
        foreach (var c in Content)
        {
            if (c == null) continue;
            if (c.Type == "audio")
                kinds.Add("audio" + (string.IsNullOrEmpty(c.MimeType) ? "" : $" ({c.MimeType})"));
            else if (c.Type == "resource" && c.Resource is { Blob: { Length: > 0 } } r
                     && !(r.MimeType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                kinds.Add($"binary resource {r.Uri ?? ""}{(string.IsNullOrEmpty(r.MimeType) ? "" : $" ({r.MimeType})")}".TrimEnd());
        }
        return kinds;
    }
}

// ════════════════════════════════════════════
// JSON-RPC 2.0 Messages
// ════════════════════════════════════════════

/// <summary>JSON-RPC 2.0 Request (expects response).</summary>
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 Notification (no response expected, no id).</summary>
public class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 Response.</summary>
public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    public bool IsSuccess => Error == null;
}

/// <summary>JSON-RPC 2.0 Error object.</summary>
public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ════════════════════════════════════════════
// MCP Initialize Types
// ════════════════════════════════════════════

public class McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "CluadeX";

    [JsonPropertyName("version")]
    public string Version { get; set; } = System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString(3) ?? "2.0.0";
}

public class McpClientCapabilities
{
    [JsonPropertyName("roots")]
    public McpRootsCapability? Roots { get; set; }
}

public class McpRootsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; } = true;
}

public class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2025-03-26";

    [JsonPropertyName("capabilities")]
    public McpClientCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get; set; } = new();
}

public class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }

    [JsonPropertyName("resources")]
    public JsonElement? Resources { get; set; }

    [JsonPropertyName("prompts")]
    public JsonElement? Prompts { get; set; }
}

public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities? Capabilities { get; set; }

    [JsonPropertyName("serverInfo")]
    public McpClientInfo? ServerInfo { get; set; }
}

// ════════════════════════════════════════════
// MCP Server Display Model (for UI binding)
// ════════════════════════════════════════════

/// <summary>
/// Observable wrapper around McpServerConfig for UI data binding.
///
/// Arguments are edited ONE PER LINE and round-trip exactly. They used to be joined with spaces for the edit
/// box and split back on spaces when saved, so any argument containing a space (C:\Program Files\…, a
/// "--name My Server" value) came back as two arguments and was written to mcp_servers.json broken — and
/// because Toggle also went through that round trip, merely enabling/disabling a server corrupted it.
/// Fields the user did not touch are taken verbatim from the original config.
/// </summary>
public class McpServerDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _status = "stopped";
    private string _statusLabel = "";
    private int _toolCount;
    private string _toolCountText = "";
    private string _logOutput = "";
    private bool _enabled = true;
    private string _enabledLabel = "";
    private string _command = "";
    private string _argsText = "";
    private string _envString = "";

    // What the edit fields showed when this item was (re)loaded from the config — an untouched field
    // re-uses Original's value instead of re-parsing text.
    private string _loadedArgsText = "";
    private string _loadedEnvString = "";

    /// <summary>The config this item was built from (args/env are taken from here when not edited).</summary>
    public McpServerConfig Original { get; private set; } = new();

    public string Name { get; set; } = "";

    public string Command
    {
        get => _command;
        set { if (_command != value) { _command = value; OnPropertyChanged(nameof(Command)); OnPropertyChanged(nameof(CommandLineDisplay)); } }
    }

    /// <summary>Arguments, one per line (the edit box).</summary>
    public string ArgsText
    {
        get => _argsText;
        set { if (_argsText != value) { _argsText = value; OnPropertyChanged(nameof(ArgsText)); OnPropertyChanged(nameof(ArgsDisplay)); OnPropertyChanged(nameof(CommandLineDisplay)); } }
    }

    /// <summary>Arguments on one line, quoted where they contain spaces — display only, never parsed back.</summary>
    public string ArgsDisplay => FormatArgsForDisplay(ArgsFromText(_argsText, _loadedArgsText, Original.Args));

    public string CommandLineDisplay => string.IsNullOrEmpty(ArgsDisplay) ? Command : $"{Command} {ArgsDisplay}";

    public string EnvString
    {
        get => _envString;
        set { if (_envString != value) { _envString = value; OnPropertyChanged(nameof(EnvString)); } }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(nameof(Enabled)); } }
    }

    /// <summary>Raw state key used by the badge colour triggers: running / starting / reconnecting / stopped / error.</summary>
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } }
    }

    /// <summary>Localized text for <see cref="Status"/> (set by the view-model).</summary>
    public string StatusLabel
    {
        get => _statusLabel;
        set { if (_statusLabel != value) { _statusLabel = value; OnPropertyChanged(nameof(StatusLabel)); } }
    }

    public int ToolCount
    {
        get => _toolCount;
        set { if (_toolCount != value) { _toolCount = value; OnPropertyChanged(nameof(ToolCount)); } }
    }

    /// <summary>Localized "N tools" (set by the view-model).</summary>
    public string ToolCountText
    {
        get => _toolCountText;
        set { if (_toolCountText != value) { _toolCountText = value; OnPropertyChanged(nameof(ToolCountText)); } }
    }

    /// <summary>Localized enabled/disabled badge text (set by the view-model).</summary>
    public string EnabledLabel
    {
        get => _enabledLabel;
        set { if (_enabledLabel != value) { _enabledLabel = value; OnPropertyChanged(nameof(EnabledLabel)); } }
    }

    public string LogOutput
    {
        get => _logOutput;
        set { if (_logOutput != value) { _logOutput = value; OnPropertyChanged(nameof(LogOutput)); } }
    }

    /// <summary>Reload the editable fields from a config (used on create and when the config changes on disk/elsewhere).</summary>
    public void LoadFrom(McpServerConfig config)
    {
        Original = config;
        _loadedArgsText = FormatArgsOnePerLine(config.Args);
        _loadedEnvString = FormatEnv(config.Env);
        Command = config.Command;
        ArgsText = _loadedArgsText;
        EnvString = _loadedEnvString;
        Enabled = config.Enabled;
        OnPropertyChanged(nameof(ArgsDisplay));
        OnPropertyChanged(nameof(CommandLineDisplay));
    }

    /// <summary>Convert back to McpServerConfig for persistence. Untouched args/env are copied verbatim, and the
    /// fields the page doesn't edit (ManagedBy, StartupTimeoutSeconds) are carried over — a Toggle on the MCP page
    /// must not turn a CluadeX-managed entry into an ordinary one.</summary>
    public McpServerConfig ToConfig() => new()
    {
        Name = Name,
        Command = Command.Trim(),
        Args = ArgsFromText(ArgsText, _loadedArgsText, Original.Args),
        Env = EnvFromText(EnvString, _loadedEnvString, Original.Env),
        Enabled = Enabled,
        ManagedBy = Original.ManagedBy,
        StartupTimeoutSeconds = Original.StartupTimeoutSeconds,
    };

    /// <summary>Create a display item from config + runtime status.</summary>
    public static McpServerDisplayItem FromConfig(string name, McpServerConfig config, bool isRunning, int toolCount)
    {
        var item = new McpServerDisplayItem
        {
            Name = name,
            Status = isRunning ? "running" : "stopped",
            ToolCount = toolCount,
        };
        item.LoadFrom(config);
        return item;
    }

    // ─── Argument / env text <-> list (public static so the harness can prove the round trip) ───

    /// <summary>One argument per line. Exact inverse of <see cref="ParseArgsOnePerLine"/> for any list of single-line args.</summary>
    public static string FormatArgsOnePerLine(IEnumerable<string>? args) => string.Join("\n", args ?? Enumerable.Empty<string>());

    /// <summary>
    /// Each line is one argument, verbatim (only a CR from a pasted CRLF is removed). Blank lines are dropped,
    /// trailing ones included — a stray Enter at the end of the box must not add an empty argument.
    /// </summary>
    public static List<string> ParseArgsOnePerLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        return text.Split('\n')
            .Select(l => l.EndsWith('\r') ? l[..^1] : l)
            .Where(l => l.Trim().Length > 0)
            .ToList();
    }

    /// <summary>
    /// Split a single command-line style string (the Add form): whitespace separates arguments, "double quotes"
    /// group one that contains spaces. Backslashes are literal so Windows paths need no escaping. Text with more
    /// than one line is read one argument per line instead.
    /// </summary>
    public static List<string> ParseArgsLine(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        if (text.Contains('\n')) return ParseArgsOnePerLine(text);

        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, has = false;
        foreach (char c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; has = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (has) { result.Add(cur.ToString()); cur.Clear(); has = false; }
                continue;
            }
            cur.Append(c);
            has = true;
        }
        if (has) result.Add(cur.ToString());
        return result;
    }

    /// <summary>One line, an argument with whitespace shown in quotes. For display only.</summary>
    public static string FormatArgsForDisplay(IEnumerable<string>? args) =>
        string.Join(" ", (args ?? Enumerable.Empty<string>())
            .Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    public static string FormatEnv(Dictionary<string, string>? env) =>
        env == null ? "" : string.Join("\n", env.Select(kv => $"{kv.Key}={kv.Value}"));

    public static Dictionary<string, string>? ParseEnv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var env = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int idx = line.IndexOf('=');
            if (idx > 0 && line[..idx].Trim().Length > 0)
                env[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return env.Count == 0 ? null : env;
    }

    private static List<string> ArgsFromText(string text, string loadedText, List<string>? original) =>
        text == loadedText && original != null ? new List<string>(original) : ParseArgsOnePerLine(text);

    private static Dictionary<string, string>? EnvFromText(string text, string loadedText, Dictionary<string, string>? original) =>
        text == loadedText ? (original == null ? null : new Dictionary<string, string>(original)) : ParseEnv(text);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new(name));
}
