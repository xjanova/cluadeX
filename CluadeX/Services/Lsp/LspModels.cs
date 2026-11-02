namespace CluadeX.Services.Lsp;

/// <summary>LSP DiagnosticSeverity (1 = error … 4 = hint). A diagnostic without a severity counts as an error, which
/// is what the specification tells a client to assume.</summary>
public enum LspSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

/// <summary>One problem a language server published for a file. Positions are LSP positions — 0-based line, 0-based
/// UTF-16 character — which are exactly AvalonEdit's (and .NET's) string indices, so no re-encoding is needed.</summary>
public sealed record EditorDiagnostic(
    int StartLine, int StartCharacter, int EndLine, int EndCharacter,
    LspSeverity Severity, string Message, string Source, string Code);

/// <summary>One incremental edit in LSP terms: the range is in the document as it was BEFORE this edit, and a list of
/// them applies in order — the same meaning AvalonEdit's Document.Changing event has, so the editor can report
/// keystrokes one by one and the service sends them as they came.</summary>
public sealed record LspTextChange(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Text);

public enum LspServerState
{
    /// <summary>No language server is known for this file type (Markdown, JSON, …) — nothing to show.</summary>
    NotSupported,
    /// <summary>The language is supported but none of its servers is installed: show the install command.</summary>
    NotInstalled,
    Starting,
    Running,
    /// <summary>The server would not start, or stopped by itself. Detail says why; Restart tries again.</summary>
    Failed,
    /// <summary>The file is too big to send on every pause in typing; live checking is off for it.</summary>
    TooLarge,
}

/// <summary>What the editor shows about the language server of the file in front of it.</summary>
public sealed record LanguageServerStatus(
    string LanguageKey, string LanguageName, LspServerState State,
    string? ServerName, string? InstallCommand, string? InstallUrl, string? Detail)
{
    public static readonly LanguageServerStatus None = new("", "", LspServerState.NotSupported, null, null, null, null);
}
