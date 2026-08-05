using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// SecurityShield — static-analysis scanner (Sprint 3 #1).
///
/// v1 (this commit):
///   - 25 ship-1 regex rules adapted from the user's global SECURITY_REVIEW
///     trap list in ~/.claude/CLAUDE.md (Top-10-style coverage)
///   - Scan-file / scan-folder API with per-finding line + snippet
///   - Background execution (Task.Run) so the UI never blocks
///   - Excludes node_modules/.git/bin/obj/etc. and files > 2 MB
///   - Logged via DebugLogService category "Security"
///
/// v2 (deferred — see ROADMAP §3.5):
///   - Red-team → blue-team → auditor subagent pipeline for deep audits
///   - YAML rule files in ~/.cluadex/security-rules/*.yaml so the user
///     can ship their own rule packs without rebuilding
///   - Auto-scan after every write_file / edit_file (file watcher)
///   - Inline squiggle integration in CodeEditorView
/// </summary>
public class SecurityShieldService
{
    private readonly FileSystemService _fs;
    private readonly DebugLogService _log;

    private List<SecurityRule>? _cachedRules;
    private readonly object _ruleLock = new();

    // Don't descend into these — too much noise + huge enumeration cost
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", "dist", "build", "out",
        ".vs", ".idea", ".vscode-test", "__pycache__", ".pytest_cache",
        "target", "venv", ".venv", "env", ".env",
        "packages", ".nuget", ".gradle",
    };

    // Only scan text-ish files. Anything bigger than this is skipped to avoid
    // memory blow-up on accidental binaries; anything outside this allowlist
    // is treated as binary.
    private const long MaxFileBytes = 2 * 1024 * 1024; // 2 MB
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs","csproj","xaml","xml","json","yaml","yml","md","txt","html","htm",
        "js","jsx","ts","tsx","mjs","cjs","vue","svelte",
        "py","rb","go","rs","java","kt","kts","swift","m","mm","c","cpp","h","hpp","cc",
        "php","pl","sh","bash","ps1","psm1","sql","ini","conf","config","env",
        "dart","lua","ex","exs","scala","groovy","gradle",
    };

    public SecurityShieldService(FileSystemService fs, DebugLogService log)
    {
        _fs = fs;
        _log = log;
    }

    // ─── Rule registry ───────────────────────────────────────────────

    public List<SecurityRule> GetAllRules()
    {
        lock (_ruleLock)
        {
            _cachedRules ??= GetBuiltInRules();
            return _cachedRules;
        }
    }

    public void ReloadRules()
    {
        lock (_ruleLock) { _cachedRules = null; }
    }

    public int BuiltInRuleCount => GetAllRules().Count(r => r.IsBuiltIn);

    // ─── Scanning ────────────────────────────────────────────────────

    /// <summary>Scan a single file. Returns empty list on binary/too-large/error.</summary>
    public List<SecurityFinding> ScanFile(string filePath)
    {
        var findings = new List<SecurityFinding>();
        try
        {
            if (!File.Exists(filePath)) return findings;
            var fi = new FileInfo(filePath);
            if (fi.Length > MaxFileBytes) return findings;

            string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (!TextExtensions.Contains(ext)) return findings;

            string[] lines;
            try { lines = File.ReadAllLines(filePath); }
            catch { return findings; }

            var rules = GetAllRules().Where(r => r.Enabled).ToList();
            foreach (var rule in rules)
            {
                if (rule.Languages.Count > 0 && !rule.Languages.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;
                var regex = BuildRegex(rule);
                if (regex == null) continue;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    if (regex.IsMatch(line))
                    {
                        findings.Add(new SecurityFinding
                        {
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            Severity = rule.Severity,
                            Category = rule.Category,
                            Cwe = rule.Cwe,
                            FilePath = filePath,
                            LineNumber = i + 1,
                            LineSnippet = Truncate(line.TrimEnd(), 200),
                            Fix = rule.Fix,
                            Description = rule.Description,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Security", $"ScanFile failed on {filePath}", ex);
        }
        return findings;
    }

    /// <summary>Scan a folder recursively (skips excluded dirs + non-text + > 2 MB).</summary>
    public async Task<SecurityScanResult> ScanFolderAsync(string rootPath, CancellationToken ct = default)
    {
        var result = new SecurityScanResult { RootPath = rootPath };
        var sw = Stopwatch.StartNew();
        _log.Info("Security", $"Starting scan: {rootPath}");

        try
        {
            await Task.Run(() =>
            {
                foreach (var file in EnumerateScannableFiles(rootPath))
                {
                    ct.ThrowIfCancellationRequested();
                    result.FilesScanned++;
                    var findings = ScanFile(file);
                    result.Findings.AddRange(findings);
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            _log.Info("Security", "Scan cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("Security", "Scan failed", ex);
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        result.Findings = result.Findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.LineNumber)
            .ToList();

        _log.Info("Security",
            $"Scan finished: {result.FilesScanned} files, {result.Findings.Count} findings " +
            $"({result.CriticalCount} crit, {result.HighCount} high, {result.MediumCount} med, " +
            $"{result.LowCount} low) in {result.Duration.TotalMilliseconds:F0}ms");
        return result;
    }

    private IEnumerable<string> EnumerateScannableFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs = Array.Empty<string>();
            IEnumerable<string> files = Array.Empty<string>();
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch { /* permission / disposed — skip */ }

            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith(".") && name != ".cluadex" && name != ".claude") continue;
                if (ExcludedDirs.Contains(name)) continue;
                stack.Push(d);
            }
            foreach (var f in files)
            {
                yield return f;
            }
        }
    }

    private static Regex? BuildRegex(SecurityRule rule)
    {
        try
        {
            var opts = RegexOptions.Compiled;
            if (!rule.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            return new Regex(rule.Pattern, opts);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ─── Built-in 25 ship-1 rules (Sprint 3 #1) ─────────────────────
    // Adapted from the user's global SECURITY_REVIEW trap table in
    // ~/.claude/CLAUDE.md. Each rule is conservative — false positives
    // are worse than false negatives in a nudge tool.

    private static List<SecurityRule> GetBuiltInRules() => new()
    {
        // Secrets — Critical
        new() { Id="sec-001", Name="Hardcoded SHA-256/private key blob", Severity=SecuritySeverity.Critical,
            Pattern=@"0x[a-fA-F0-9]{64}", Category="Secrets", Cwe="CWE-798",
            Fix="Move to env var / secret manager. Never commit raw 64-hex-char blobs.",
            Description="A 64-character hex literal looks like a private key, signing secret, or hash. Even if it's a hash, putting it in source ties the build to one secret." },
        new() { Id="sec-002", Name="Hardcoded API key (sk-/AKIA/etc.)", Severity=SecuritySeverity.Critical,
            Pattern=@"\b(sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{30,}|xox[abp]-[A-Za-z0-9-]{10,})\b",
            Category="Secrets", Cwe="CWE-798",
            Fix="Rotate the leaked key immediately, then move to env var.",
            Description="Looks like an OpenAI / AWS / GitHub / Slack token committed to source." },
        new() { Id="sec-003", Name="Password assigned to literal string", Severity=SecuritySeverity.High,
            Pattern=@"(?:password|passwd|secret|api[_-]?key|token)\s*[:=]\s*['""][^'""$\{][^'""\n]{3,}['""]",
            Category="Secrets", Cwe="CWE-798",
            Fix="Use Environment.GetEnvironmentVariable / DPAPI / secret manager.",
            Description="Looks like a credential literal in code." },

        // Logging secrets — High
        new() { Id="sec-010", Name="console.log of secret-like variable", Severity=SecuritySeverity.High,
            Pattern=@"(?:console\.log|print|println!?|Debug\.WriteLine|System\.out\.println)\s*\([^)]*(?:password|secret|token|api[_-]?key|mnemonic|private[_-]?key|pin)\b",
            Languages=new(){"js","jsx","ts","tsx","py","rs","cs","java","go"},
            Category="Logging", Cwe="CWE-532",
            Fix="Never log raw credentials. Mask with `***` or omit entirely.",
            Description="Logging credentials leaks them to log files / crash reporters." },

        // Injection — Critical / High
        new() { Id="sec-020", Name="SQL string concat / interpolation", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:SELECT|INSERT|UPDATE|DELETE|DROP)\b.*[+`].*\$?\{?[A-Za-z_]\w*\}?",
            CaseSensitive=false, Category="Injection", Cwe="CWE-89",
            Fix="Use parameterised queries / prepared statements (@p, ?, :name).",
            Description="SQL built via string concat or interpolation is the textbook SQLi vector." },
        new() { Id="sec-021", Name="Shell command via string concat", Severity=SecuritySeverity.High,
            Pattern=@"(?:exec|spawn|Process\.Start|os\.system|subprocess\.(?:call|run|Popen)|sh\s+-c)\s*\([^)]*[+`$]",
            Category="Injection", Cwe="CWE-78",
            Fix="Pass args as an array, never as a single shell string.",
            Description="Shell built via concat lets attacker-controlled input inject extra commands." },
        new() { Id="sec-022", Name="dangerouslySetInnerHTML / innerHTML with variable", Severity=SecuritySeverity.High,
            Pattern=@"(?:dangerouslySetInnerHTML|\.innerHTML)\s*[:=]",
            Languages=new(){"js","jsx","ts","tsx","html"},
            Category="Injection", Cwe="CWE-79",
            Fix="Use textContent for plain text, or sanitise via DOMPurify / sanitize-html.",
            Description="Setting innerHTML from a non-constant string is the XSS surface." },
        new() { Id="sec-023", Name="eval / Function() with variable", Severity=SecuritySeverity.High,
            Pattern=@"\b(?:eval|new\s+Function|Function\()\s*\([^)]*[A-Za-z_]\w*\s*\)",
            Languages=new(){"js","jsx","ts","tsx","py"},
            Category="Injection", Cwe="CWE-95",
            Fix="Refactor to remove eval. If truly necessary, validate input against an allowlist.",
            Description="eval() of user data = arbitrary code execution." },

        // Crypto — High / Medium
        new() { Id="sec-030", Name="Weak hash (MD5 / SHA-1)", Severity=SecuritySeverity.Medium,
            Pattern=@"\b(?:MD5|SHA1|MessageDigest\.getInstance\(\s*['""]?(?:MD5|SHA1)['""]?\))",
            Category="Crypto", Cwe="CWE-327",
            Fix="Use SHA-256+. MD5/SHA-1 are broken for security purposes (still ok for non-security checksums).",
            Description="MD5 and SHA-1 are collision-broken — never use for passwords, signatures, or integrity checks." },
        new() { Id="sec-031", Name="ECB mode cipher", Severity=SecuritySeverity.High,
            Pattern=@"\b(?:AES|DES|Cipher)/?\.?ECB\b|CipherMode\.ECB",
            Category="Crypto", Cwe="CWE-327",
            Fix="Use GCM (preferred) or CBC + HMAC. ECB leaks patterns in plaintext.",
            Description="ECB mode encrypts each block independently — patterns in plaintext show through." },
        new() { Id="sec-032", Name="Random for crypto purpose", Severity=SecuritySeverity.High,
            Pattern=@"\b(?:Math\.random|new\s+Random\(\)|random\.random\(\)|rand\(\))\s*\(\)",
            Category="Crypto", Cwe="CWE-338",
            Fix="Use a CSPRNG: RandomNumberGenerator (.NET), crypto.randomBytes (Node), secrets (Python).",
            Description="Math.random / Random() are NOT cryptographically secure. Predictable for tokens / keys." },

        // String comparison for secrets — High
        new() { Id="sec-040", Name="== comparison on secret-like name", Severity=SecuritySeverity.Medium,
            Pattern=@"(?:password|token|secret|hash|signature|hmac)\b.*={2,3}",
            Category="Auth", Cwe="CWE-208",
            Fix="Use constant-time compare: CryptographicOperations.FixedTimeEquals / crypto.timingSafeEqual / hmac.compare_digest.",
            Description="String equality short-circuits on first mismatch — timing leaks how many chars matched." },

        // Path traversal — High
        new() { Id="sec-050", Name="Path joined with user input + no validation", Severity=SecuritySeverity.High,
            Pattern=@"Path\.(?:Combine|Join)\s*\([^)]*(?:request|req\.|input|params|body|query)\b",
            Languages=new(){"cs","js","jsx","ts","tsx"},
            Category="PathTraversal", Cwe="CWE-22",
            Fix="Validate the path is under your intended root after joining: Path.GetFullPath(combined).StartsWith(root).",
            Description="Joining user input into a file path without validation allows `../../etc/passwd` style escapes." },

        // Deserialisation — Critical / High
        new() { Id="sec-060", Name="Unsafe deserialisation: pickle.loads / yaml.load", Severity=SecuritySeverity.Critical,
            Pattern=@"\b(?:pickle\.loads?|yaml\.load\s*\((?!.*Loader\s*=\s*(?:SafeLoader|safe_load)))",
            Languages=new(){"py"},
            Category="Deserialize", Cwe="CWE-502",
            Fix="pickle: don't. yaml: use yaml.safe_load.",
            Description="pickle and yaml.load(default Loader) execute arbitrary code from the input." },
        new() { Id="sec-061", Name="BinaryFormatter / SoapFormatter (.NET)", Severity=SecuritySeverity.Critical,
            Pattern=@"\bnew\s+(?:BinaryFormatter|SoapFormatter|NetDataContractSerializer)\b",
            Languages=new(){"cs"},
            Category="Deserialize", Cwe="CWE-502",
            Fix="Switch to System.Text.Json. BinaryFormatter is officially obsolete and dangerous.",
            Description="BinaryFormatter and its siblings are known RCE vectors." },

        // Network / TLS — High / Medium
        new() { Id="sec-070", Name="HTTP (not HTTPS) URL literal in production-like file", Severity=SecuritySeverity.Low,
            Pattern=@"['""]http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|::1)[^'""\s]+['""]",
            Category="Other", Cwe="CWE-319",
            Fix="Use HTTPS unless this is a known internal-only endpoint.",
            Description="Plain HTTP in source code = MITM target." },
        new() { Id="sec-071", Name="Disabled TLS validation", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:rejectUnauthorized\s*[:=]\s*false|verify\s*=\s*False|ServerCertificateValidationCallback\s*[+]?=\s*\(.*=>\s*true|ServicePointManager\.ServerCertificateValidationCallback\s*[+]?=)",
            Category="Auth", Cwe="CWE-295",
            Fix="Never disable TLS validation in production. Pin the cert if you must trust a self-signed origin.",
            Description="Disabled TLS validation makes the whole connection unauthenticated." },

        // Access control — High
        new() { Id="sec-080", Name="Missing auth attribute on controller action", Severity=SecuritySeverity.Medium,
            Pattern=@"^\s*public\s+(?:async\s+)?[A-Za-z<>,\s]+\s+\w+Async?\s*\(.*\)\s*$",
            Languages=new(){"cs"},
            Category="Auth", Cwe="CWE-862",
            Fix="Add [Authorize] / [AllowAnonymous] explicitly on every public controller method.",
            Description="ASP.NET Core controllers without an explicit auth attribute fall back to whatever the global default is — easy to forget." },

        // Hardcoded debug / dev artifacts — Medium / Low
        new() { Id="sec-090", Name="DEBUG = true / NODE_ENV != production in code", Severity=SecuritySeverity.Low,
            Pattern=@"\bDEBUG\s*=\s*True\b|\bNODE_ENV\s*[:=]\s*['""]development['""]",
            Category="Hardcoded", Cwe="CWE-489",
            Fix="Read from env var / config, never hardcode.",
            Description="Hardcoded debug flag shipped to prod = stack traces, verbose errors, dev creds." },
        new() { Id="sec-091", Name="TODO / FIXME / HACK with security keyword", Severity=SecuritySeverity.Info,
            Pattern=@"//\s*(?:TODO|FIXME|HACK|XXX).*(?:auth|password|token|security|cred|crypto)",
            Category="Other",
            Fix="Open a ticket and link it. Drift-prone comments rot fastest in security code.",
            Description="A security-tagged TODO that's lasted past one sprint is an audit finding." },

        // Error handling — Medium
        new() { Id="sec-100", Name="Bare except / catch swallowing exception", Severity=SecuritySeverity.Low,
            Pattern=@"(?:except\s*:\s*pass|catch\s*\(\s*\)\s*\{\s*\}|catch\s*\(.*\)\s*\{\s*\})",
            Category="Other", Cwe="CWE-754",
            Fix="Log + decide. Bare catches hide real bugs.",
            Description="Empty except / catch hides failures that may include security-relevant errors." },
        new() { Id="sec-101", Name="Raw exception / stack trace returned to user", Severity=SecuritySeverity.Medium,
            Pattern=@"return\s+(?:ex\.ToString\(\)|str\(e\)|e\.message|exception\.toString\(\))",
            Category="Other", Cwe="CWE-209",
            Fix="Return a generic message to the user; log the stack trace server-side.",
            Description="Stack traces in HTTP responses leak file paths, versions, and internal structure." },

        // Rate limiting — Low
        new() { Id="sec-110", Name="Public endpoint with no rate-limit attribute", Severity=SecuritySeverity.Info,
            Pattern=@"^\s*\[(?:HttpGet|HttpPost|HttpPut|HttpDelete|Route)\b",
            Languages=new(){"cs"},
            Category="Auth", Cwe="CWE-770",
            Fix="Add [EnableRateLimiting] or middleware-level rate limit on public endpoints.",
            Description="Heads-up scan: every public endpoint needs a rate-limit story. May be a false-positive if rate limiting is global." },

        // CORS — Medium
        new() { Id="sec-120", Name="CORS allow any origin (`*`)", Severity=SecuritySeverity.Medium,
            Pattern=@"(?:Access-Control-Allow-Origin\s*[:=]\s*['""]?\*|AllowAnyOrigin\s*\(\s*\)|origin\s*:\s*['""]?\*)",
            Category="Auth", Cwe="CWE-942",
            Fix="Allowlist specific origins. `*` + credentials is especially dangerous.",
            Description="CORS `*` lets any site send authenticated requests on the user's behalf." },

        // Cookies — Medium
        new() { Id="sec-130", Name="Cookie set without Secure / HttpOnly", Severity=SecuritySeverity.Medium,
            Pattern=@"new\s+Cookie\b|Set-Cookie:|res\.cookie\(|response\.set_cookie\(",
            Category="Auth", Cwe="CWE-614",
            Fix="Set Secure + HttpOnly + SameSite=Strict on every session/auth cookie.",
            Description="Heads-up scan for cookie creation — verify Secure/HttpOnly/SameSite are set." },

        // ── Secrets & key material ───────────────────────────────────────────────
        new() { Id="sec-140", Name="Private key block committed to source", Severity=SecuritySeverity.Critical,
            Pattern=@"-----BEGIN\s+(RSA|EC|DSA|OPENSSH|PGP)?\s*PRIVATE KEY-----",
            Category="Secrets", Cwe="CWE-798",
            Fix="Remove the key, rotate it, and load it from a key store or env var at runtime.",
            Description="A PEM private key header in source means the key is in your VCS history forever." },
        new() { Id="sec-141", Name="Mnemonic / seed phrase in source", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:mnemonic|seed[_-]?phrase|recovery[_-]?phrase)\s*[:=]\s*['""][a-z ]{20,}['""]",
            Category="Secrets", Cwe="CWE-798",
            Fix="Never hardcode a wallet mnemonic. Load from encrypted storage entered by the user.",
            Description="Looks like a BIP-39 mnemonic literal — anyone with the repo owns the wallet." },
        new() { Id="sec-142", Name="Connection string with inline password", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:Server|Data Source|Host)\s*=[^;'""\n]+;[^'""\n]*(?:Password|Pwd)\s*=\s*[^;'""\s]+",
            Category="Secrets", Cwe="CWE-798",
            Fix="Use integrated auth or a secret manager; keep the password out of the literal.",
            Description="A database connection string with an embedded password." },
        new() { Id="sec-143", Name="Secret printed to logs", Severity=SecuritySeverity.High,
            Pattern=@"(?:print|console\.log|Debug\.WriteLine|logger?\.(?:info|debug|warn|error)|printf)\s*\([^)\n]*\b(?:password|passwd|mnemonic|private[_-]?key|secret|api[_-]?key|token|pin)\b",
            Category="Logging", Cwe="CWE-532",
            Fix="Redact before logging — log an identifier, never the credential itself.",
            Description="Credential-looking value flows into a log sink; logs are usually world-readable and shipped off-box." },

        // ── Injection ────────────────────────────────────────────────────────────
        new() { Id="sec-150", Name="SQL built by string concatenation", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:SELECT|INSERT|UPDATE|DELETE)\s+[^;'""\n]*['""]\s*\+|""\s*\+\s*\w+\s*\+\s*""\s*(?:WHERE|FROM|VALUES)",
            Category="Injection", Cwe="CWE-89",
            Fix="Use parameterised queries / prepared statements. Never concatenate user input into SQL.",
            Description="SQL assembled with string concatenation is the classic injection vector." },
        new() { Id="sec-151", Name="Shell command built from a variable", Severity=SecuritySeverity.Critical,
            Pattern=@"(?:os\.system|subprocess\.(?:call|run|Popen)|exec|execSync|spawnSync|Runtime\.getRuntime\(\)\.exec)\s*\(\s*(?:f?['""][^'""\n]*\{|['""][^'""\n]*['""]\s*\+|\w+\s*\+)",
            Category="Injection", Cwe="CWE-78",
            Fix="Pass an argument array instead of a command string, and validate every interpolated value.",
            Description="Interpolating a variable into a shell command line allows command injection." },
        new() { Id="sec-152", Name="shell=True with interpolated command", Severity=SecuritySeverity.High,
            Pattern=@"shell\s*=\s*True",
            Category="Injection", Cwe="CWE-78", Languages=new(){"py"},
            Fix="Drop shell=True and pass a list of arguments.",
            Description="shell=True hands the string to /bin/sh — any metacharacter in user data becomes code." },
        new() { Id="sec-153", Name="innerHTML / dangerouslySetInnerHTML with dynamic value", Severity=SecuritySeverity.High,
            Pattern=@"(?:innerHTML|outerHTML)\s*=\s*(?!['""]\s*['""])[^;'""\n]*[\w\)]|dangerouslySetInnerHTML",
            Category="Injection", Cwe="CWE-79",
            Fix="Use textContent, or sanitise with DOMPurify before injecting HTML.",
            Description="Assigning dynamic content as HTML is the standard DOM-XSS sink." },
        new() { Id="sec-154", Name="eval / Function constructor on dynamic input", Severity=SecuritySeverity.Critical,
            Pattern=@"\beval\s*\(|new\s+Function\s*\(|setTimeout\s*\(\s*['""]",
            Category="Injection", Cwe="CWE-95",
            Fix="Parse data with JSON.parse; never evaluate strings as code.",
            Description="eval turns any attacker-controlled string into executable code." },
        new() { Id="sec-155", Name="LDAP / XPath query concatenation", Severity=SecuritySeverity.High,
            Pattern=@"(?:SelectNodes|SelectSingleNode|DirectorySearcher|ldap_search)\s*\([^)\n]*\+",
            Category="Injection", Cwe="CWE-90",
            Fix="Escape the filter value or use a parameterised query API.",
            Description="Concatenated LDAP/XPath filters can be broken out of by the supplied value." },

        // ── Crypto ───────────────────────────────────────────────────────────────
        new() { Id="sec-160", Name="Weak hash (MD5 / SHA-1) used", Severity=SecuritySeverity.High,
            Pattern=@"\b(?:MD5|SHA1|Sha1|md5|sha1)(?:\.Create\(\)|CryptoServiceProvider|\s*\()",
            Category="Crypto", Cwe="CWE-327",
            Fix="Use SHA-256+ for integrity, and Argon2/bcrypt/PBKDF2 for passwords.",
            Description="MD5 and SHA-1 are collision-broken and must not be used for security decisions." },
        new() { Id="sec-161", Name="ECB mode or static IV", Severity=SecuritySeverity.High,
            Pattern=@"CipherMode\.ECB|MODE_ECB|/ECB/|new\s+byte\[\s*16\s*\]\s*;?\s*(?://.*)?$\s*.*\.IV\s*=",
            Category="Crypto", Cwe="CWE-327",
            Fix="Use an AEAD mode (AES-GCM / ChaCha20-Poly1305) with a fresh random nonce per message.",
            Description="ECB leaks plaintext structure; a fixed IV destroys CBC/CTR security." },
        new() { Id="sec-162", Name="Insecure randomness for security value", Severity=SecuritySeverity.High,
            Pattern=@"new\s+Random\s*\(|Math\.random\s*\(|random\.randint\s*\(|rand\s*\(\s*\)",
            Category="Crypto", Cwe="CWE-338",
            Fix="Use RandomNumberGenerator / crypto.randomBytes / secrets module for tokens, salts and IDs.",
            Description="A non-cryptographic PRNG is predictable — never use it for tokens, nonces or keys." },
        new() { Id="sec-163", Name="Password compared with ==", Severity=SecuritySeverity.Medium,
            Pattern=@"(?:password|passwd|token|hmac|signature|secret)\w*\s*(?:==|!=|\.Equals\()\s*\w",
            Category="Crypto", Cwe="CWE-208",
            Fix="Use a constant-time comparison (CryptographicOperations.FixedTimeEquals / hmac.compare_digest).",
            Description="Short-circuiting comparison leaks how many leading bytes matched, enabling timing attacks." },

        // ── Transport & TLS ──────────────────────────────────────────────────────
        new() { Id="sec-170", Name="TLS certificate validation disabled", Severity=SecuritySeverity.Critical,
            Pattern=@"ServerCertificateValidationCallback\s*(?:\+)?=\s*(?:delegate|\(|.*true)|rejectUnauthorized\s*:\s*false|verify\s*=\s*False|InsecureSkipVerify\s*:\s*true|--insecure\b|curl_setopt\([^)]*SSL_VERIFYPEER[^)]*,\s*(?:0|false)",
            Category="Crypto", Cwe="CWE-295",
            Fix="Never disable validation. For self-signed dev certs, trust the specific cert instead.",
            Description="Accepting any certificate makes every request trivially MITM-able." },
        new() { Id="sec-171", Name="Plain http:// endpoint for an API", Severity=SecuritySeverity.Medium,
            Pattern=@"['""]http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0)[a-z0-9.\-]+",
            Category="Crypto", Cwe="CWE-319",
            Fix="Use https:// — traffic over plain HTTP can be read and rewritten in transit.",
            Description="A non-local http:// endpoint sends data in cleartext." },
        new() { Id="sec-172", Name="Obsolete TLS version pinned", Severity=SecuritySeverity.High,
            Pattern=@"SecurityProtocolType\.(?:Ssl3|Tls|Tls11)\b|TLSv1(?:\.0|\.1)?['""]|PROTOCOL_TLSv1\b",
            Category="Crypto", Cwe="CWE-327",
            Fix="Require TLS 1.2 minimum, prefer TLS 1.3.",
            Description="SSLv3/TLS 1.0/1.1 are deprecated and broken." },

        // ── Access control & web ─────────────────────────────────────────────────
        new() { Id="sec-180", Name="CORS wildcard with credentials", Severity=SecuritySeverity.High,
            Pattern=@"Access-Control-Allow-Origin['""]?\s*[:,]\s*['""]\*|AllowAnyOrigin\(\)",
            Category="Auth", Cwe="CWE-942",
            Fix="Echo a specific allowed origin; never combine '*' with credentialed requests.",
            Description="A wildcard CORS policy lets any site read authenticated responses." },
        new() { Id="sec-181", Name="Authorization / CSRF check disabled", Severity=SecuritySeverity.High,
            Pattern=@"\[AllowAnonymous\]|csrf\s*[:=]\s*(?:False|false)|@csrf_exempt|IgnoreAntiforgeryToken",
            Category="Auth", Cwe="CWE-352",
            Fix="Confirm this endpoint is genuinely public; otherwise restore the check.",
            Description="An explicitly disabled auth/CSRF guard — every one needs a stated reason." },
        new() { Id="sec-182", Name="JWT decoded without signature verification", Severity=SecuritySeverity.Critical,
            Pattern=@"verify\s*[:=]\s*(?:False|false)|jwt\.decode\([^)]*verify_signature['""]?\s*:\s*False|ValidateIssuerSigningKey\s*=\s*false|algorithms\s*=\s*\[\s*['""]none['""]",
            Category="Auth", Cwe="CWE-347",
            Fix="Always verify the signature and pin the expected algorithm.",
            Description="An unverified JWT is attacker-controlled data, not an identity." },
        new() { Id="sec-183", Name="Missing rate limit on an auth endpoint", Severity=SecuritySeverity.Medium,
            Pattern=@"(?:app\.(?:post|get)|\[HttpPost\]|@app\.route)[^)\n]*(?:login|signin|register|reset[_-]?password|verify[_-]?otp)",
            Category="Auth", Cwe="CWE-307",
            Fix="Add per-IP and per-account rate limiting plus lockout/backoff.",
            Description="Heads-up: authentication endpoints without a rate limit are brute-force targets." },

        // ── Files, deserialization & resource safety ─────────────────────────────
        new() { Id="sec-190", Name="Path joined from user input without containment", Severity=SecuritySeverity.High,
            Pattern=@"(?:Path\.Combine|os\.path\.join|path\.join)\s*\([^)\n]*(?:req\.|request\.|input|argv|params|query|body)",
            Category="PathTraversal", Cwe="CWE-22",
            Fix="Resolve to a full path and verify it stays under the allowed root before opening it.",
            Description="Joining request data into a path allows ../ traversal out of the intended folder." },
        new() { Id="sec-191", Name="Archive extracted without entry-path validation", Severity=SecuritySeverity.High,
            Pattern=@"ExtractToDirectory\s*\(|extractall\s*\(|\.extract\s*\(",
            Category="PathTraversal", Cwe="CWE-22",
            Fix="Validate each entry's resolved path stays inside the target directory (zip-slip).",
            Description="Zip-slip: an archive entry named ../../x overwrites files outside the extract folder." },
        new() { Id="sec-192", Name="XML parser with external entities enabled", Severity=SecuritySeverity.High,
            Pattern=@"XmlResolver\s*=\s*new\s+XmlUrlResolver|DtdProcessing\.Parse|resolve_entities\s*=\s*True|XMLParser\(.*resolve_entities",
            Category="Deserialize", Cwe="CWE-611",
            Fix="Disable DTD/entity resolution (XmlResolver = null, DtdProcessing.Prohibit).",
            Description="XXE lets a crafted document read local files or reach internal hosts." },
        new() { Id="sec-193", Name="Unbounded read of user-supplied input", Severity=SecuritySeverity.Medium,
            Pattern=@"ReadToEnd\s*\(\s*\)|\.read\s*\(\s*\)\s*$|ReadAllBytes\s*\(",
            Category="Other", Cwe="CWE-400",
            Fix="Cap the size before reading, and stream instead of buffering whole payloads.",
            Description="Reading an entire request/file into memory with no cap is a denial-of-service lever." },
        new() { Id="sec-194", Name="Exception detail returned to the caller", Severity=SecuritySeverity.Medium,
            Pattern=@"(?:return|send|write|Json)\s*\([^)\n]*(?:ex\.(?:ToString\(\)|StackTrace|Message)|traceback\.format_exc\(\)|err\.stack)",
            Category="Logging", Cwe="CWE-209",
            Fix="Log the detail server-side; return a generic message plus a correlation id.",
            Description="Stack traces and raw exception text reveal paths, versions and query shapes to an attacker." },
    };
}
