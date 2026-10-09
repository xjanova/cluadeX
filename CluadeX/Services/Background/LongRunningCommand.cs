using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Background;

/// <summary>
/// The run_command / powershell interceptor for commands that never exit. A dev server sent to run_command used to
/// block the turn for 120 s and then be killed; a watcher the same. Now:
/// <list type="bullet">
/// <item>a SERVER (npm run dev — judged by what the script runs, vite, python -m http.server, php -S, uvicorn,
///   docker compose up …, or anything ending in a lone <c>&amp;</c>, <c>nohup</c>, or Start-Process -NoNewWindow
///   without -Wait) is started in the background with an inferred port, and the call returns as soon as it is ready:
///   "Started in background as bg#N → URL". A plain Start-Process detaches by itself and is left alone, so what it
///   opens outlives CluadeX;</item>
/// <item>a WATCHER (tsc -w, jest --watch, vitest watch, webpack --watch, tail -f) is refused at once, naming the
///   one-shot form (plain vitest runs once here: its stdin is not a terminal);</item>
/// <item>with Dev Preview locked, both fail in well under a second with the reason and the Terminal-drawer
///   alternative — never a 120 s hang;</item>
/// <item>a command that timed out after printing a localhost URL is called what it is: a server.</item>
/// </list>
/// It runs after the permission prompt, the PreToolUse hooks and the dangerous-command check, so the command was
/// already approved exactly as written — the background start asks nothing new.
/// </summary>
public sealed class LongRunningCommand : IShellCommandInterceptor
{
    private readonly BackgroundProcessService _bg;
    private readonly SettingsService _settings;
    private readonly ActivationService _activation;
    private readonly IServiceProvider? _services;

    public LongRunningCommand(BackgroundProcessService background, SettingsService settings, ActivationService activation,
        IServiceProvider? services = null)
    {
        _bg = background;
        _settings = settings;
        _activation = activation;
        _services = services;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string L(string key, params object[] args) => string.Format(L(key), args);

    // ═══════════════════════════ the interceptor ═══════════════════════════

    public async Task<ToolResult?> BeforeRunAsync(ShellCommandContext context, CancellationToken ct)
    {
        // No project open: run_command answers that itself ("No project folder is open") — a server must never
        // start in the home folder instead.
        if (string.IsNullOrEmpty(context.ProjectDirectory)) return null;
        string cwd = context.WorkingDirectory ?? context.ProjectDirectory;
        var c = Classify(context.Command, context.Shell, cwd);
        if (c.Kind == CommandKind.None
            && string.Equals(context.Call.GetArg("run_in_background"), "true", StringComparison.OrdinalIgnoreCase))
            c = c with { Kind = CommandKind.Background };
        if (c.Kind == CommandKind.None) return null;

        string? refusal = DevPreviewGate.Refusal(_settings, _activation, "bg_start");
        // A guess from a script's NAME alone (package.json unreadable, or no such script) never blocks a user without
        // Dev Preview: it runs as it always did — a real server that times out is still called a server (OnTimeout).
        if (c.Guessed && refusal != null) return null;
        if (c.Kind == CommandKind.Watcher)
        {
            string text = L("bg.watcher.refused", c.Tool ?? "", c.OneShot ?? L("bg.watcher.noWatchFlag"));
            if (refusal == null) text += " " + L("bg.watcher.useBgStart");
            return Fail(context.Call, text);
        }

        if (refusal != null)
            return Fail(context.Call, L("bg.locked.server", c.Tool ?? "", refusal));

        int requested = context.TimeoutMs > 0 ? context.TimeoutMs : 120_000;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(requested, 3_000, c.Port != null ? 60_000 : 15_000));
        if (c.Kind == CommandKind.Background && c.Port == null) timeout = TimeSpan.FromSeconds(2);

        var result = await _bg.StartAsync(new BgStartRequest
        {
            Command = c.Command,
            Cwd = cwd,
            Shell = context.Shell,
            Port = c.Port,
            PortExplicit = c.PortExplicit,
            ReadyTimeout = timeout,
            StartedBy = "agent",
        }, ct).ConfigureAwait(false);
        return BackgroundToolModule.FormatStart(result, context.Call, BrowserOpenAvailable(), fromRunCommand: true, ShellOutputBudget());
    }

    /// <summary>The output budget run_command itself uses (AgentToolService.ShellOutputBudget): 30,000 characters for
    /// an API model, a quarter-context share (4,000-12,000) for a local one.</summary>
    private int ShellOutputBudget()
    {
        var s = _settings.Settings;
        if (s.ActiveProvider is not (AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama)) return 30_000;
        int read = Math.Clamp((int)Math.Max(2048, s.ContextSize), 6_000, 100_000);
        return Math.Clamp(read / 2, 4_000, 12_000);
    }

    public string? OnTimeout(ShellCommandContext context, string output)
    {
        if (ReadinessProbe.FindLocalUrl(output) is not { } found) return null;
        string? refusal = DevPreviewGate.Refusal(_settings, _activation, "bg_start");
        return refusal == null
            ? L("bg.timeout.server", found.Url)
            : L("bg.timeout.serverLocked", found.Url, refusal);
    }

    /// <summary>Some built-in browser tool can open a bg:N URL right now (T3's browser_open, when offered).</summary>
    internal bool BrowserOpenAvailable()
    {
        if (_services == null) return false;
        try
        {
            foreach (var m in _services.GetServices<IAgentToolModule>())
            {
                if (m is BackgroundToolModule) continue;
                try
                {
                    if (m.ResolveToolType("browser_open") is { } t && m.ToolTypes.Contains(t) && m.GetRefusal(t) == null) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static ToolResult Fail(ToolCall call, string error) => new()
    {
        Type = call.Type, ToolName = call.ToolName, Success = false, Error = error, Summary = error.Split('\n')[0],
    };

    // ═══════════════════════════ classification ═══════════════════════════

    public enum CommandKind { None, Server, Watcher, Background }

    /// <summary>What a command is: a server (with the port it will likely use), a watcher (with its one-shot form),
    /// something explicitly backgrounded, or an ordinary command. <see cref="Guessed"/>: only the name of an npm /
    /// yarn script said so — its text could not be read.</summary>
    public sealed record Classification(CommandKind Kind, string Command, int? Port = null, bool PortExplicit = false,
        string? Tool = null, string? OneShot = null, bool Guessed = false);

    /// <summary>
    /// Classify <paramref name="command"/> as written for <paramref name="shell"/>. Word-based, never substring:
    /// "grep vite package.json", "echo npm run dev" and a commit message mentioning vitest are ordinary commands.
    /// <paramref name="cwd"/> lets "npm run dev" read package.json to learn what the script runs.
    /// </summary>
    public static Classification Classify(string command, ShellRunner.Shell shell, string? cwd = null) =>
        Classify(command ?? "", shell, cwd, depth: 0);

    private static Classification Classify(string command, ShellRunner.Shell shell, string? cwd, int depth)
    {
        var segments = Tokenize(command, shell, out bool trailingAmp);
        string cleaned = trailingAmp ? StripTrailingAmp(command) : command.Trim();
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? dir = cwd;

        Classification? server = null, watcher = null;
        bool background = trailingAmp;
        foreach (var words in segments)
        {
            if (words.Count == 0) continue;
            var seg = ClassifySegment(words, ref dir, env, shell, depth, ref background);
            if (seg == null) continue;
            if (seg.Kind == CommandKind.Server && server == null) server = seg;
            else if (seg.Kind == CommandKind.Watcher && watcher == null) watcher = seg;
        }

        if (server != null)
        {
            int? port = server.Port;
            bool explicitPort = server.PortExplicit;
            if (!explicitPort && env.TryGetValue("PORT", out var p) && int.TryParse(p, out var envPort) && envPort is > 0 and < 65536)
            {
                port = envPort;
                explicitPort = true;
            }
            return server with { Command = cleaned, Port = port, PortExplicit = explicitPort };
        }
        if (watcher != null) return watcher with { Command = cleaned };
        if (background) return new Classification(CommandKind.Background, cleaned, Tool: FirstWord(segments));
        return new Classification(CommandKind.None, command);
    }

    private static string? FirstWord(List<List<string>> segments) => segments.FirstOrDefault(s => s.Count > 0)?.FirstOrDefault();

    private static readonly HashSet<string> PlainWrappers = new(StringComparer.OrdinalIgnoreCase)
        { "sudo", "doas", "time", "exec", "command", "builtin", "nice", "stdbuf", "unbuffer" };

    private static readonly HashSet<string> ServerScriptNames = new(StringComparer.OrdinalIgnoreCase)
        { "dev", "start", "serve", "server", "preview", "develop", "dev:server", "start:dev", "serve:dev" };

    private static readonly HashSet<string> YarnBuiltins = new(StringComparer.OrdinalIgnoreCase)
        { "add", "install", "remove", "upgrade", "init", "info", "list", "link", "unlink", "why", "cache", "config",
          "global", "outdated", "pack", "publish", "version", "audit", "build", "create", "dlx", "exec", "workspace", "workspaces" };

    /// <summary>One simple command (words already unquoted). Null = nothing notable (a cd, an export, a plain command).</summary>
    private static Classification? ClassifySegment(List<string> words, ref string? dir, Dictionary<string, string> env,
        ShellRunner.Shell shell, int depth, ref bool background)
    {
        int i = 0;
        // Leading NAME=value assignments (bash), and wrappers that run the rest of the line.
        while (true)
        {
            while (i < words.Count && TryAssignment(words[i], env)) i++;
            if (i >= words.Count) return null;
            string w = Base(words[i]);
            string next = i + 1 < words.Count ? Base(words[i + 1]) : "";
            if (PlainWrappers.Contains(w)) { i++; continue; }
            if (w == "nohup") { background = true; i++; continue; }
            if (w is "env" or "cross-env" or "cross-env-shell") { i++; continue; }
            if (w is "npx" or "bunx" or "pnpx")
            {
                i++;
                while (i < words.Count && words[i].StartsWith('-')) i++;
                continue;
            }
            if ((w is "pnpm" or "yarn") && next is "exec" or "dlx") { i += 2; continue; }
            if (w == "npm" && next is "exec" or "x") { i += 2; while (i < words.Count && words[i].StartsWith('-')) i++; continue; }
            if (w == "bundle" && next == "exec") { i += 2; continue; }
            if ((w is "poetry" or "pipenv" or "uv" or "rye" or "pdm" or "hatch") && next == "run") { i += 2; continue; }
            if (w == "export") return CaptureAssignments(words, i + 1, env);
            if (w == "set" && shell != ShellRunner.Shell.PowerShell) return CaptureAssignments(words, i + 1, env);
            if (w == "cd" || w == "pushd" || w == "set-location" || w == "sl")
            {
                if (i + 1 < words.Count && dir != null)
                {
                    try { dir = Path.GetFullPath(Path.Combine(dir, words[i + 1].Replace('/', Path.DirectorySeparatorChar))); } catch { }
                }
                return null;
            }
            if (w is "start-process" or "saps" || (w == "start" && shell == ShellRunner.Shell.PowerShell))
            {
                // Start-Process detaches by itself (ShellExecute: its own window or none, no inherited pipes), so the
                // call returns at once and what it opened — an editor, a game, a browser — must outlive CluadeX: it is
                // never put in a job. Only -NoNewWindow or a -RedirectStandard* (CreateProcess with this shell's
                // handles) WITHOUT -Wait keeps the pipes open and would hang the call.
                var rest = words.Skip(i + 1).ToList();
                bool wait = rest.Any(a => IsSwitch(a, "-wait", 3));
                bool inheritsPipes = rest.Any(a => IsSwitch(a, "-nonewwindow", 2) || IsSwitch(a, "-nnw", 4)
                                                   || a.StartsWith("-red", StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("-rso", StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("-rse", StringComparison.OrdinalIgnoreCase)
                                                   || a.Equals("-rsi", StringComparison.OrdinalIgnoreCase));
                if (inheritsPipes && !wait) background = true;
                return null;
            }
            break;
        }

        string prog = Base(words[i]);
        var args = words.Skip(i + 1).ToList();
        string sub = args.FirstOrDefault(a => !a.StartsWith('-')) is { } s ? s.ToLowerInvariant() : "";

        Classification Server(int? defaultPort, string tool, params string[] portFlags)
        {
            var (port, explicitPort) = FlagPort(args, portFlags);
            return new Classification(CommandKind.Server, "", port ?? defaultPort, explicitPort, tool);
        }
        Classification Watcher(string tool, string oneShot) => new(CommandKind.Watcher, "", Tool: tool, OneShot: oneShot);
        bool Has(params string[] flags) => args.Any(a => flags.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase)
                                                                     || a.StartsWith(f + "=", StringComparison.OrdinalIgnoreCase)));
        bool HasFalse(string flag) => args.Any(a => a.Equals(flag + "=false", StringComparison.OrdinalIgnoreCase));

        switch (prog)
        {
            // ── python ──
            case "python" or "python3" or "py" or "pythonw":
            {
                int m = args.FindIndex(a => a == "-m");
                if (m >= 0 && m + 1 < args.Count)
                {
                    string module = args[m + 1].ToLowerInvariant();
                    var rest = args.Skip(m + 2).ToList();
                    switch (module)
                    {
                        case "http.server" or "simplehttpserver":
                        {
                            // python -m http.server [-b ADDRESS] [-d DIR] [-p VERSION] [port]
                            int? positional = null;
                            for (int k = 0; k < rest.Count; k++)
                            {
                                string a = rest[k];
                                if (a is "-b" or "--bind" or "-d" or "--directory" or "-p" or "--protocol") { k++; continue; }
                                if (a.StartsWith('-')) continue;
                                if (int.TryParse(a, out var n) && IsPortValue(n)) { positional = n; break; }
                            }
                            return new Classification(CommandKind.Server, "", positional ?? 8000, positional != null, "python -m http.server");
                        }
                        case "uvicorn" or "hypercorn":
                            return ServerFrom(rest, 8000, "uvicorn", "--port");
                        case "flask" when rest.Any(a => a == "run"):
                            return ServerFrom(rest, 5000, "flask run", "--port", "-p");
                        case "django" when rest.Any(a => a == "runserver"):
                            return RunServer(rest, "django runserver");
                        case "streamlit" when rest.Any(a => a == "run"):
                            return ServerFrom(rest, 8501, "streamlit run", "--server.port");
                        case "mkdocs" when rest.Any(a => a == "serve"):
                            return ServerFrom(rest, 8000, "mkdocs serve", "--dev-addr", "-a");
                        case "pytest_watch" or "ptw":
                            return Watcher("pytest-watch", "python -m pytest");
                    }
                    return null;
                }
                int script = args.FindIndex(a => !a.StartsWith('-'));
                if (script >= 0 && args[script].EndsWith("manage.py", StringComparison.OrdinalIgnoreCase)
                    && args.Skip(script + 1).FirstOrDefault()?.Equals("runserver", StringComparison.OrdinalIgnoreCase) == true)
                    return RunServer(args.Skip(script + 2).ToList(), "manage.py runserver");
                return null;
            }
            case "manage.py" when sub == "runserver":
                return RunServer(args.Skip(1).ToList(), "manage.py runserver");
            case "django-admin" when sub == "runserver":
                return RunServer(args.Skip(1).ToList(), "django-admin runserver");
            case "uvicorn" or "hypercorn":
                return Server(8000, prog, "--port");
            case "gunicorn":
                return ServerFrom(args, 8000, "gunicorn", "--bind", "-b");
            case "waitress-serve":
                return ServerFrom(args, 8080, "waitress-serve", "--port", "--listen");
            case "flask" when sub == "run":
                return Server(5000, "flask run", "--port", "-p");
            case "streamlit" when sub == "run":
                return Server(8501, "streamlit run", "--server.port");
            case "mkdocs" when sub == "serve":
                return ServerFrom(args, 8000, "mkdocs serve", "--dev-addr", "-a");
            case "jupyter" when sub is "notebook" or "lab":
                return Server(8888, "jupyter " + sub, "--port");

            // ── node package managers: what does the script run? ──
            case "npm":
            {
                string? script = sub switch
                {
                    "start" => "start",
                    "test" or "t" or "tst" => "test",
                    "run" or "run-script" or "rum" or "urn" => args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')),
                    _ => null,
                };
                return script == null ? null : FromScript("npm", script, dir, shell, depth);
            }
            case "pnpm" or "yarn" or "bun":
            {
                if (sub.Length == 0) return null;
                string? script = sub == "run" ? args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) : sub;
                if (script == null) return null;
                if (prog == "yarn" && sub != "run" && YarnBuiltins.Contains(script)) return null;
                if (prog == "pnpm" && sub is "install" or "i" or "add" or "remove" or "update" or "build" or "publish") return null;
                if (prog == "bun" && sub is "install" or "i" or "add" or "remove" or "build" or "x" or "test" or "upgrade" or "pm" or "init" or "create")
                    return null;
                return FromScript(prog, script, dir, shell, depth);
            }
            case "concurrently" or "npm-run-all" or "run-p" or "run-s":
            {
                Classification? found = null;
                foreach (var a in args.Where(a => !a.StartsWith('-')))
                {
                    var inner = prog == "concurrently" ? Classify(a, ShellRunner.Shell.Cmd, dir, depth + 1)
                                                       : FromScript("npm", a, dir, shell, depth);
                    if (inner?.Kind == CommandKind.Server) return inner;
                    found ??= inner?.Kind == CommandKind.Watcher ? inner : null;
                }
                return found;
            }

            // ── front-end dev servers ──
            case "vite":
                // "sub" may be a flag's value ("vite --port 5175"), so only the one-shot subcommands are excluded.
                if (sub == "build") return Has("--watch", "-w") ? Watcher("vite build --watch", "vite build") : null;
                if (sub == "optimize") return null;
                if (sub == "preview") return Server(4173, "vite preview", "--port");
                return Server(5173, "vite", "--port");
            case "next":
                return sub is "dev" or "start" ? Server(3000, "next " + sub, "--port", "-p") : null;
            case "nuxt" or "nuxi":
                return sub is "dev" or "preview" or "start" ? Server(3000, "nuxt " + sub, "--port") : null;
            case "astro":
                return sub is "dev" or "preview" or "start" ? Server(4321, "astro " + sub, "--port") : null;
            case "remix":
                return sub == "dev" ? Server(3000, "remix dev", "--port") : null;
            case "svelte-kit":
                return sub == "dev" ? Server(5173, "svelte-kit dev", "--port") : null;
            case "gatsby":
                return sub is "develop" or "serve" ? Server(sub == "serve" ? 9000 : 8000, "gatsby " + sub, "--port", "-p") : null;
            case "ng":
                if (sub is "serve" or "s") return Server(4200, "ng serve", "--port");
                if (sub is "test" or "t" && !HasFalse("--watch") && !Has("--no-watch")) return Watcher("ng test", "ng test --watch=false");
                return null;
            case "react-scripts":
                if (sub == "start") return Server(3000, "react-scripts start");
                if (sub == "test" && !Has("--watchAll=false") && !HasFalse("--watch")) return Watcher("react-scripts test", "react-scripts test --watchAll=false");
                return null;
            case "webpack" or "webpack-cli":
                if (sub == "serve") return Server(8080, "webpack serve", "--port");
                if (Has("--watch", "-w")) return Watcher("webpack --watch", "webpack");
                return null;
            case "webpack-dev-server":
                return Server(8080, "webpack-dev-server", "--port");
            case "parcel":
                if (sub == "watch") return Watcher("parcel watch", "parcel build");
                if (sub is "build") return null;
                return Server(1234, "parcel", "--port", "-p");
            case "http-server":
                return Server(8080, "http-server", "--port", "-p");
            case "serve":
                return Server(3000, "serve", "--listen", "-l", "--port", "-p");
            case "live-server" or "five-server":
                return Server(8080, prog, "--port");
            case "browser-sync" when sub == "start":
                return Server(3000, "browser-sync", "--port");
            case "json-server":
                return Server(3000, "json-server", "--port", "-p");
            case "eleventy" or "@11ty/eleventy" when Has("--serve"):
                return Server(8080, "eleventy --serve", "--port");
            case "storybook" when sub == "dev":
                return Server(6006, "storybook dev", "--port", "-p");
            case "start-storybook":
                return Server(6006, "start-storybook", "--port", "-p");
            case "nodemon" or "ts-node-dev" or "node-dev":
                return Server(null, prog);
            case "tsx" when sub == "watch":
                return Server(null, "tsx watch");
            case "node" when Has("--watch"):
                return Server(null, "node --watch");

            // ── other stacks ──
            case "php":
                if (args.FindIndex(a => a == "-S") is var si && si >= 0 && si + 1 < args.Count)
                {
                    var (port, _) = HostPort(args[si + 1]);
                    return new Classification(CommandKind.Server, "", port ?? 8000, port != null, "php -S");
                }
                if (args.Count >= 2 && args[0].EndsWith("artisan", StringComparison.OrdinalIgnoreCase) && args[1] == "serve")
                    return Server(8000, "php artisan serve", "--port");
                return null;
            case "artisan" when sub == "serve":
                return Server(8000, "artisan serve", "--port");
            case "rails" or "rail":
                return sub is "server" or "s" ? Server(3000, "rails server", "--port", "-p") : null;
            case "jekyll":
                return sub is "serve" or "s" or "server" ? Server(4000, "jekyll serve", "--port", "-P") : null;
            case "hugo":
                return sub is "server" or "serve" ? Server(1313, "hugo server", "--port", "-p") : null;
            case "dotnet":
                if (sub == "watch")
                {
                    string inner = args.SkipWhile(a => !a.Equals("watch", StringComparison.OrdinalIgnoreCase)).Skip(1)
                                       .FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "run";
                    return inner == "run" ? Server(null, "dotnet watch run") : Watcher("dotnet watch " + inner, "dotnet " + inner);
                }
                return null;
            case "docker" when sub == "compose" && args.Any(a => a == "up") && !Has("-d", "--detach"):
                return Server(null, "docker compose up");
            case "docker-compose" when sub == "up" && !Has("-d", "--detach"):
                return Server(null, "docker-compose up");
            case "firebase" when sub is "serve" or "emulators:start":
                return Server(5000, "firebase " + sub, "--port", "-p");

            // ── watchers ──
            case "tsc" when Has("-w", "--watch"):
                return Watcher("tsc --watch", "tsc --noEmit");
            case "jest" when (Has("--watch", "--watchAll") && !HasFalse("--watch") && !HasFalse("--watchAll")):
                return Watcher("jest --watch", "jest --watchAll=false");
            case "vitest":
                // vitest watches only when stdin is a terminal and CI is unset (watch = !CI && stdin.isTTY). The
                // stdin of run_command is not a terminal, so plain "vitest" — and "npm test" running it — runs once.
                // Only an explicit watch never ends.
                if (sub is "watch" or "dev" || (Has("--watch", "-w") && !HasFalse("--watch")))
                    return Watcher("vitest watch", "vitest run");
                return null;
            case "mocha" when Has("--watch", "-w"):
                return Watcher("mocha --watch", "mocha");
            case "karma" when sub == "start" && !Has("--single-run"):
                return Watcher("karma start", "karma start --single-run");
            case "rollup" when Has("-w", "--watch"):
                return Watcher("rollup --watch", "rollup -c");
            case "esbuild" when Has("--watch"):
                return Watcher("esbuild --watch", "esbuild (without --watch)");
            case "sass" or "node-sass" when Has("--watch", "-w"):
                return Watcher(prog + " --watch", prog + " (without --watch)");
            case "tailwindcss" or "tailwind" when Has("--watch", "-w"):
                return Watcher("tailwindcss --watch", "tailwindcss (without --watch)");
            case "babel" when Has("--watch", "-w"):
                return Watcher("babel --watch", "babel (without --watch)");
            case "cargo" when sub == "watch":
                return Watcher("cargo watch", "cargo build / cargo test");
            case "tail" when Has("-f", "-F", "--follow"):
                return Watcher("tail -f", "tail -n 100 <file>");
            case "get-content" or "gc" or "cat" when shell == ShellRunner.Shell.PowerShell && Has("-Wait"):
                return Watcher("Get-Content -Wait", "Get-Content -Tail 100 <file>");
            case "watch":
                return Watcher("watch", L("bg.watcher.runOnce"));
        }
        return null;
    }

    private static Classification ServerFrom(List<string> args, int defaultPort, string tool, params string[] flags)
    {
        // Flags that carry host:port (gunicorn -b 0.0.0.0:8000, mkdocs -a 127.0.0.1:8001, waitress --listen=*:8080).
        for (int i = 0; i < args.Count; i++)
        {
            foreach (var f in flags)
            {
                string? value = null;
                if (args[i].Equals(f, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) value = args[i + 1];
                else if (args[i].StartsWith(f + "=", StringComparison.OrdinalIgnoreCase)) value = args[i][(f.Length + 1)..];
                if (value == null) continue;
                var (port, _) = HostPort(value);
                if (port != null) return new Classification(CommandKind.Server, "", port, true, tool);
            }
        }
        return new Classification(CommandKind.Server, "", defaultPort, false, tool);
    }

    private static Classification RunServer(List<string> rest, string tool)
    {
        // runserver [addrport]: "8001", "0.0.0.0:8001", "127.0.0.1:8001"
        var a = rest.FirstOrDefault(x => !x.StartsWith('-'));
        var (port, _) = a != null ? HostPort(a) : (null, null);
        return new Classification(CommandKind.Server, "", port ?? 8000, port != null, tool);
    }

    /// <summary>"8000", ":8000", "0.0.0.0:8000", "[::1]:8000", "localhost:8000" → 8000.</summary>
    private static (int? Port, string? Host) HostPort(string value)
    {
        string v = value.Trim();
        int colon = v.LastIndexOf(':');
        string portText = colon >= 0 ? v[(colon + 1)..] : v;
        return int.TryParse(portText, out var p) && IsPortValue(p) ? (p, colon > 0 ? v[..colon] : null) : (null, null);
    }

    private static bool IsPortValue(int n) => n is > 0 and < 65536;

    private static (int? Port, bool Explicit) FlagPort(List<string> args, params string[] flags)
    {
        for (int i = 0; i < args.Count; i++)
        {
            foreach (var f in flags)
            {
                string? value = null;
                if (args[i].Equals(f, StringComparison.Ordinal) && i + 1 < args.Count) value = args[i + 1];
                else if (args[i].StartsWith(f + "=", StringComparison.Ordinal)) value = args[i][(f.Length + 1)..];
                else if (f.Length == 2 && f[0] == '-' && args[i].Length > 2 && args[i].StartsWith(f, StringComparison.Ordinal)
                         && args[i][2..].All(char.IsDigit)) value = args[i][2..];   // -p8080
                if (value != null && int.TryParse(value, out var p) && IsPortValue(p)) return (p, true);
            }
        }
        return (null, false);
    }

    /// <summary>"npm run X" / "yarn X": what the script X in package.json runs decides. When that says nothing — the
    /// script runs a program not recognised ("node server.js", a CLI tool), or its text cannot be read — the script's
    /// NAME guesses (dev / start / serve → a server, watch → a watcher), marked <see cref="Classification.Guessed"/>:
    /// with Dev Preview a guessed "server" that is really a one-shot simply finishes in the background and its whole
    /// output comes back; without Dev Preview a guess never blocks anything (it runs as it always did).</summary>
    private static Classification? FromScript(string manager, string script, string? dir, ShellRunner.Shell shell, int depth)
    {
        string runner = manager == "npm" ? (script is "start" or "test" ? $"npm {script}" : $"npm run {script}") : $"{manager} {script}";
        string? text = depth < 3 ? ScriptText(dir, script) : null;
        if (text != null)
        {
            // npm runs scripts with cmd.exe on Windows.
            var inner = Classify(text, ShellRunner.Shell.Cmd, dir, depth + 1);
            if (inner.Kind != CommandKind.None) return inner with { Tool = $"{runner} ({inner.Tool})" };
        }
        if (ServerScriptNames.Contains(script)) return new Classification(CommandKind.Server, "", null, false, runner, Guessed: true);
        if (script.Equals("watch", StringComparison.OrdinalIgnoreCase) || script.EndsWith(":watch", StringComparison.OrdinalIgnoreCase))
            return new Classification(CommandKind.Watcher, "", Tool: runner, OneShot: manager == "npm" ? "npm run build" : $"{manager} build", Guessed: true);
        return null;
    }

    /// <summary>A PowerShell switch as typed: "-Wait", "-wa" (an unambiguous prefix, at least
    /// <paramref name="minLength"/> characters), "-Wait:$true" — but not "-Wait:$false".</summary>
    private static bool IsSwitch(string word, string full, int minLength)
    {
        string w = word.Trim();
        string value = "";
        int colon = w.IndexOf(':');
        if (colon >= 0) { value = w[(colon + 1)..]; w = w[..colon]; }
        if (w.Length < minLength || !full.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return false;
        return !value.Equals("$false", StringComparison.OrdinalIgnoreCase) && value != "0";
    }

    private static string? ScriptText(string? dir, string script)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            string file = Path.Combine(dir, "package.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty(script, out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();
        }
        catch { }
        return null;
    }

    private static bool TryAssignment(string word, Dictionary<string, string> env)
    {
        int eq = word.IndexOf('=');
        if (eq <= 0) return false;
        string name = word[..eq];
        if (!name.All(ch => char.IsLetterOrDigit(ch) || ch == '_') || char.IsDigit(name[0])) return false;
        env[name] = word[(eq + 1)..];
        return true;
    }

    private static Classification? CaptureAssignments(List<string> words, int from, Dictionary<string, string> env)
    {
        for (int i = from; i < words.Count; i++) TryAssignment(words[i], env);
        return null;
    }

    /// <summary>The program name of a word: "./node_modules/.bin/vite.cmd" → "vite", "C:\Python\python.exe" → "python".</summary>
    private static string Base(string word)
    {
        string w = word.Trim();
        int slash = Math.Max(w.LastIndexOf('/'), w.LastIndexOf('\\'));
        if (slash >= 0 && slash < w.Length - 1) w = w[(slash + 1)..];
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (w.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { w = w[..^ext.Length]; break; }
        return w.ToLowerInvariant();
    }

    private static string StripTrailingAmp(string command)
    {
        string t = command.TrimEnd();
        if (t.EndsWith('&') && !t.EndsWith("&&", StringComparison.Ordinal)) t = t[..^1].TrimEnd();
        return t;
    }

    /// <summary>
    /// Split a command line into simple commands (words unquoted) at ; &amp;&amp; || | &amp; and newlines, outside quotes.
    /// <paramref name="trailingAmp"/>: it ends in a lone &amp; (run in the background). Good enough to find the program
    /// of each simple command — not a full shell parser.
    /// </summary>
    internal static List<List<string>> Tokenize(string command, ShellRunner.Shell shell, out bool trailingAmp)
    {
        var segments = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        string lastSep = "";
        bool afterSep = false;
        char escape = shell switch { ShellRunner.Shell.Bash => '\\', ShellRunner.Shell.PowerShell => '`', _ => '^' };

        void EndWord()
        {
            if (inWord) words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }
        void EndSegment(string sep)
        {
            EndWord();
            segments.Add(words);
            words = new List<string>();
            lastSep = sep;
            afterSep = true;
        }

        string s = command ?? "";
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == escape && i + 1 < s.Length && shell != ShellRunner.Shell.Cmd)
            {
                word.Append(s[++i]);
                inWord = true;
                afterSep = false;
                continue;
            }
            if (ch == '\'' && shell != ShellRunner.Shell.Cmd)
            {
                int end = s.IndexOf('\'', i + 1);
                if (end < 0) end = s.Length;
                word.Append(s, i + 1, Math.Max(0, end - i - 1));
                inWord = true;
                afterSep = false;
                i = end;
                continue;
            }
            if (ch == '"')
            {
                int j = i + 1;
                for (; j < s.Length && s[j] != '"'; j++)
                {
                    if (s[j] == '\\' && shell == ShellRunner.Shell.Bash && j + 1 < s.Length) { word.Append(s[++j]); continue; }
                    word.Append(s[j]);
                }
                inWord = true;
                afterSep = false;
                i = j;
                continue;
            }
            if (ch == '#' && !inWord && shell != ShellRunner.Shell.Cmd)
            {
                int nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl - 1;
                continue;
            }
            if (char.IsWhiteSpace(ch) && ch != '\n') { EndWord(); continue; }
            if (ch == '\n' || ch == ';' || ch == '(' || ch == ')') { EndSegment(ch == '\n' ? "\n" : ch.ToString()); continue; }
            if (ch == '|')
            {
                EndSegment(i + 1 < s.Length && s[i + 1] == '|' ? "||" : "|");
                if (lastSep == "||") i++;
                continue;
            }
            if (ch == '&')
            {
                if (i + 1 < s.Length && s[i + 1] == '&') { EndSegment("&&"); i++; continue; }
                if (shell == ShellRunner.Shell.Bash && i + 1 < s.Length && s[i + 1] == '>') { word.Append(ch); inWord = true; continue; }   // &> file
                if (i > 0 && s[i - 1] == '>') { word.Append(ch); inWord = true; continue; }                                                 // 2>&1
                // PowerShell: '&' starting a command is the call operator, not a background marker.
                if (shell == ShellRunner.Shell.PowerShell && !inWord && (words.Count == 0)
                    && s[(i + 1)..].Trim().Length > 0) continue;
                EndSegment("&");
                continue;
            }
            word.Append(ch);
            inWord = true;
            afterSep = false;
        }
        EndWord();
        if (words.Count > 0) segments.Add(words);
        trailingAmp = lastSep == "&" && afterSep && words.Count == 0;
        return segments.Where(w => w.Count > 0).ToList();
    }
}
