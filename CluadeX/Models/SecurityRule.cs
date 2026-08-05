namespace CluadeX.Models;

/// <summary>
/// One static-analysis rule. Pattern is a regex applied line-by-line
/// against source. v1 is regex-only; v2 will add AST/semantic checks via
/// language-specific subagents (architect / security-reviewer subagent
/// pipeline per ROADMAP §3.5).
/// </summary>
public class SecurityRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public SecuritySeverity Severity { get; set; } = SecuritySeverity.Medium;

    /// <summary>Regex pattern applied per-line. Case-insensitive by default.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>If true the regex is case-sensitive.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>File extensions this rule applies to (lowercase, no leading dot). Empty = all text files.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Optional CWE / OWASP id for reporting.</summary>
    public string? Cwe { get; set; }

    /// <summary>One-line hint shown in the finding card.</summary>
    public string? Fix { get; set; }

    /// <summary>Logical category bucket: "Secrets", "Injection", "Crypto", "Auth", etc. Drives chip colour.</summary>
    public string Category { get; set; } = "Other";

    public bool IsBuiltIn { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public enum SecuritySeverity
{
    /// <summary>Style / clean-up.</summary>
    Info,
    /// <summary>Suspicious but not exploitable on its own.</summary>
    Low,
    /// <summary>Should be fixed before next release.</summary>
    Medium,
    /// <summary>Likely exploitable in a realistic threat model.</summary>
    High,
    /// <summary>RCE / auth bypass / data exfil / hardcoded prod secret.</summary>
    Critical,
}

/// <summary>One match of a rule against a file.</summary>
public class SecurityFinding
{
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public SecuritySeverity Severity { get; set; }
    public string Category { get; set; } = "";
    public string? Cwe { get; set; }

    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string LineSnippet { get; set; } = "";
    public string? Fix { get; set; }
    public string Description { get; set; } = "";

    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string SeverityLabel => Severity.ToString().ToUpperInvariant();
    public string Location => $"{FileName}:{LineNumber}";

    public string SeverityColorHex => Severity switch
    {
        SecuritySeverity.Critical => "#FF5EC4",
        SecuritySeverity.High     => "#FF6E6E",
        SecuritySeverity.Medium   => "#FFD166",
        SecuritySeverity.Low      => "#4CDFFF",
        SecuritySeverity.Info     => "#8388BD",
        _                          => "#8388BD",
    };

    public string CategoryColorHex => Category switch
    {
        "Secrets"    => "#FF6E6E",
        "Injection"  => "#FF5EC4",
        "Crypto"     => "#A672FF",
        "Auth"       => "#FFD166",
        "PathTraversal" => "#FF8AA6",
        "Deserialize" => "#FF6E6E",
        "Logging"    => "#FFD166",
        "Hardcoded"  => "#FF8AA6",
        _            => "#4CDFFF",
    };
}

/// <summary>Result of scanning a file / folder.</summary>
public class SecurityScanResult
{
    public string RootPath { get; set; } = "";
    public int FilesScanned { get; set; }
    public int FilesSkipped { get; set; }
    public List<SecurityFinding> Findings { get; set; } = new();
    public TimeSpan Duration { get; set; }

    public int CriticalCount => Findings.Count(f => f.Severity == SecuritySeverity.Critical);
    public int HighCount => Findings.Count(f => f.Severity == SecuritySeverity.High);
    public int MediumCount => Findings.Count(f => f.Severity == SecuritySeverity.Medium);
    public int LowCount => Findings.Count(f => f.Severity == SecuritySeverity.Low);
}
