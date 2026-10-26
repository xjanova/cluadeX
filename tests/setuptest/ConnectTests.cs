using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CluadeX.Services.Connections;
using CluadeX.Services.Setup;

// Connections (GitHub / Stripe / Canva through their official MCP servers, BrainX first) and the one-click BrainX
// install. Nothing is installed and no real config is touched: bridges files are temp copies, downloads are served by a
// fake handler. The one live check (GitHub's release + checksum) only reads public GitHub release files.
static partial class Program
{
    static ConnectorDef Def(string id) => ConnectorCatalog.Find(id)!;

    static List<ConnectorFacts> FakeConnections() => new()
    {
        new ConnectorFacts { Def = Def("github"), GhInstalled = true, GhSignedIn = true, ServerReady = true, Place = ConnectorPlace.BrainX, Live = true, BrainAvailable = true },
        new ConnectorFacts { Def = Def("stripe"), Node = new Version(24, 11, 1), BrainAvailable = true },
        new ConnectorFacts { Def = Def("canva"), Node = new Version(24, 11, 1), ManualPlace = ConnectorPlace.CluadeX, BrainAvailable = true },
    };

    static IEnumerable<string> ConnectionTextKeys()
    {
        foreach (var c in ConnectorCatalog.All) { yield return c.GuideKey; }
        foreach (var k in new[] { "connect.state.connected", "connect.place.brainx", "connect.place.cluadex", "connect.place.manualBrainx",
                     "connect.place.manualCluadex", "connect.place.notLive", "connect.place.willBrainx", "connect.place.willCluadex",
                     "connect.guide.title", "connect.guide.ok", "connect.guide.open", "connect.confirm.go", "connect.confirm.cancel",
                     "connect.key.title", "connect.key.prompt", "connect.key.save", "connect.key.bad", "connect.key.liveTitle", "connect.key.liveBody",
                     "connect.key.useAnyway", "connect.note.ghInstall", "connect.note.ghSignIn", "connect.note.download", "connect.note.nodeInstall",
                     "connect.note.registering", "connect.note.brainx", "connect.note.cluadex", "connect.note.canvaFirst", "connect.note.disconnected",
                     "connect.note.stillNoGh", "connect.note.stillNoSignIn", "connect.note.stillNoNode", "connect.disconnect.title", "connect.disconnect.body",
                     "connect.err.noServer", "connect.err.signIn", "connect.err.noNode", "connect.err.noKey", "connect.err.unknown", "connect.err.noMcp",
                     "connect.err.mcpUnreadable", "connect.err.noAsset", "connect.err.checksum", "connect.err.download",
                     "brainx.confirm.title", "brainx.confirm.body", "brainx.confirm.go", "brainx.note.downloading", "brainx.note.checking",
                     "brainx.note.running", "brainx.note.waiting", "brainx.note.comingSoon", "brainx.note.done", "brainx.note.notYet", "brainx.unsigned.title",
                     "brainx.unsigned.body", "brainx.unsigned.run", "brainx.err.host", "brainx.err.notFile", "brainx.err.size", "brainx.err.http",
                     "brainx.err.signature", "brainx.err.run" })
            yield return k;
    }

    const string OwnerBridges = """
        {
            "$schema":  "https://brainx.local/mcp-bridges.schema.json",
            "_readme":  [ "External MCP servers the brain connects OUT to." ],
            "version":  1,
            "bridges":  [
                { "id": "unity", "enabled": true, "command": "C:\\Users\\x\\.local\\bin\\mcp-for-unity.exe", "args": ["--transport", "stdio"], "env": {}, "timeoutSeconds": 180 },
                { "id": "unreal", "enabled": true, "url": "http://127.0.0.1:8077/mcp", "probe": { "kind": "http", "port": 8077 } }
            ]
        }
        """;

    static async Task ConnectionTests()
    {
        Console.WriteLine("── connections: the catalog ──");
        Check("C1", "four services, each with an id BrainX accepts as a bridge id ([a-z0-9.-], ≤16, no __)",
            ConnectorCatalog.All.Count == 4 && ConnectorCatalog.All.All(c => c.Id.Length <= 16 && c.Id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.') && !c.Id.Contains("__")));
        var stripe = Def("stripe");
        Check("C2", "a Stripe key is checked before it is saved (prefix, length, no spaces)",
            ConnectorCatalog.LooksLikeKey(stripe, "rk_test_" + new string('a', 24)) && !ConnectorCatalog.LooksLikeKey(stripe, "pk_live_" + new string('a', 24))
            && !ConnectorCatalog.LooksLikeKey(stripe, "rk_test_short") && !ConnectorCatalog.LooksLikeKey(stripe, "rk_test_aaaa aaaaaaaaaaaaaaaaaa")
            && !ConnectorCatalog.LooksLikeKey(stripe, null));
        Check("C3", "a live secret key (sk_live_) is flagged; a restricted one is not",
            ConnectorCatalog.IsLiveSecretKey("sk_live_x") && !ConnectorCatalog.IsLiveSecretKey("rk_live_x"));
        Check("C4", "connections the user set up by hand are recognised (GitHub binary / remote, Stripe npx, Canva url, our launcher)",
            ConnectorCatalog.Recognises(Def("github"), @"C:\tools\github-mcp-server.exe", new[] { "stdio" })
            && ConnectorCatalog.Recognises(Def("github"), "npx", new[] { "mcp-remote", "https://api.githubcopilot.com/mcp/" })
            && ConnectorCatalog.Recognises(stripe, "npx", new[] { "-y", "@stripe/mcp", "--api-key=x" })
            && ConnectorCatalog.Recognises(Def("canva"), null, null, "https://mcp.canva.com/mcp")
            && ConnectorCatalog.Recognises(Def("xdreamer"), null, null, "https://ai.xman4289.com/api/mcp")
            && ConnectorCatalog.Recognises(Def("xdreamer"), "npx", new[] { "-y", "mcp-remote", "https://ai.xman4289.com/api/mcp" })
            && ConnectorCatalog.Recognises(stripe, @"C:\x\CluadeX.exe", new[] { "--connector", "stripe" })
            && !ConnectorCatalog.Recognises(stripe, @"C:\x\CluadeX.exe", new[] { "--connector", "canva" })
            && !ConnectorCatalog.Recognises(Def("github"), "uvx", new[] { "mcp-for-unity" }));
        Check("C5", "node versions are read (v24.11.1, 22.16.0) and compared with Canva's minimum",
            ConnectorCatalog.ParseNodeVersion("v24.11.1\n") is { Major: 24 } v && v >= ConnectorCatalog.CanvaMinNode
            && ConnectorCatalog.ParseNodeVersion("v20.9.0") < ConnectorCatalog.CanvaMinNode && ConnectorCatalog.ParseNodeVersion("garbage") == null);

        Console.WriteLine("── connections: the launcher never puts a key on a command line ──");
        var gh = ConnectorLauncher.Plan(Def("github"), new LaunchFacts(null, @"C:\d\github-mcp-server.exe", "gho_" + new string('x', 36), null));
        Check("C6", "GitHub: the server's stdio mode, the gh token only in GITHUB_PERSONAL_ACCESS_TOKEN",
            gh.Plan is { } g && g.Args.SequenceEqual(new[] { "stdio" }) && g.Env["GITHUB_PERSONAL_ACCESS_TOKEN"].StartsWith("gho_")
            && !g.Args.Any(a => a.Contains("gho_")));
        var st = ConnectorLauncher.Plan(stripe, new LaunchFacts(@"C:\node\npx.cmd", null, null, "rk_test_secret123456789"));
        Check("C7", "Stripe: the pinned package, the key only in STRIPE_SECRET_KEY",
            st.Plan is { } s && s.Args.SequenceEqual(new[] { "-y", ConnectorCatalog.StripePackage }) && s.Env["STRIPE_SECRET_KEY"] == "rk_test_secret123456789"
            && !s.Args.Any(a => a.Contains("rk_test")) && ConnectorCatalog.StripePackage.Contains('@', StringComparison.Ordinal) && ConnectorCatalog.StripePackage.LastIndexOf('@') > 0);
        var cv = ConnectorLauncher.Plan(Def("canva"), new LaunchFacts(@"C:\node\npx.cmd", null, null, null));
        Check("C8", "Canva: mcp-remote (pinned) to Canva's official server, no secret at all",
            cv.Plan is { } c && c.Args.SequenceEqual(new[] { "-y", ConnectorCatalog.McpRemotePackage, "https://mcp.canva.com/mcp" }) && c.Env.Count == 0);
        var xd = ConnectorLauncher.Plan(Def("xdreamer"), new LaunchFacts(null, null, null, " axm-" + new string('k', 40) + " "));
        Check("C9b", "XDREAMER: CluadeX's own HTTP relay to the official endpoint (no Node, no process), the key only in its Authorization header",
            xd.Plan is { IsRemote: true } x && x.Url == "https://ai.xman4289.com/api/mcp" && x.Exe == "" && x.Args.Count == 0 && x.Env.Count == 0
            && x.Headers!["Authorization"] == "Bearer axm-" + new string('k', 40) && Def("xdreamer").Needs == ConnectorNeed.None);
        Check("C9c", "XDREAMER: no key → named, not started; only axm- keys are taken",
            ConnectorLauncher.Plan(Def("xdreamer"), new LaunchFacts(null, null, null, null)).Error == "connect.err.noKey"
            && ConnectorCatalog.LooksLikeKey(Def("xdreamer"), "axm-" + new string('a', 40)) && !ConnectorCatalog.LooksLikeKey(Def("xdreamer"), "sk-" + new string('a', 40)));
        Check("C9", "what is missing is named, not started half-way (no server · signed out · no Node · no key)",
            ConnectorLauncher.Plan(Def("github"), new LaunchFacts(null, null, "t", null)).Error == "connect.err.noServer"
            && ConnectorLauncher.Plan(Def("github"), new LaunchFacts(null, "x.exe", null, null)).Error == "connect.err.signIn"
            && ConnectorLauncher.Plan(stripe, new LaunchFacts(null, null, null, "k")).Error == "connect.err.noNode"
            && ConnectorLauncher.Plan(stripe, new LaunchFacts("npx.cmd", null, null, null)).Error == "connect.err.noKey");

        Console.WriteLine("── connections: BrainX's bridges file (the owner's file — only our entry changes) ──");
        string dir = Path.Combine(Path.GetTempPath(), "cx-bridges-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "mcp-bridges.json");
            File.WriteAllText(file, OwnerBridges);
            var root = BrainXBridges.Load(file)!;
            bool added = BrainXBridges.Upsert(root, stripe, @"C:\Users\x\AppData\Local\CluadeX\current\CluadeX.exe");
            BrainXBridges.Save(file, root);
            var back = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var bridges = back["bridges"]!.AsArray();
            var mine = bridges.Single(b => (string?)b!["id"] == "stripe")!.AsObject();
            Check("C10", "our entry is added; unity, unreal, $schema and _readme are kept as they were",
                added && bridges.Count == 3 && back["_readme"] != null && back["$schema"] != null
                && bridges.Any(b => (string?)b!["id"] == "unity" && b["args"]!.AsArray().Count == 2) && bridges.Any(b => (string?)b!["id"] == "unreal" && b["probe"] != null));
            Check("C11", "the entry names only the launcher — env is empty (the vault syncs: no key in it)",
                (string?)mine["command"] == @"C:\Users\x\AppData\Local\CluadeX\current\CluadeX.exe"
                && mine["args"]!.AsArray().Select(a => (string?)a).SequenceEqual(new[] { "--connector", "stripe" })
                && mine["env"]!.AsObject().Count == 0 && (bool)mine["enabled"]! && !File.ReadAllText(file).Contains("rk_"));
            Check("C12", "a .bak of the owner's original is kept", File.Exists(file + ".bak") && File.ReadAllText(file + ".bak").Contains("mcp-for-unity"));
            Check("C13", "connecting again refreshes the same entry (never a second)",
                BrainXBridges.Upsert(root, stripe, @"D:\new\CluadeX.exe") && root["bridges"]!.AsArray().Count(b => (string?)b!["id"] == "stripe") == 1);

            var owners = BrainXBridges.Load(file)!;
            owners["bridges"]!.AsArray().Add(new JsonObject { ["id"] = "github", ["enabled"] = true, ["command"] = @"C:\tools\github-mcp-server.exe", ["args"] = new JsonArray("stdio") });
            Check("C14", "the owner's own 'github' bridge is never overwritten, and it is recognised as the connection",
                !BrainXBridges.Upsert(owners, Def("github"), "CluadeX.exe") && BrainXBridges.Find(owners, Def("github")) is { Entry: not null, Ours: false });
            Check("C15", "disconnect removes only CluadeX's entry", BrainXBridges.Remove(owners, stripe) && !BrainXBridges.Remove(owners, Def("github"))
                && owners["bridges"]!.AsArray().Count == 3);

            File.WriteAllText(file, "{ \"bridges\": [ { \"id\": \"unity\", ");   // half-written by hand
            Check("C16", "a bridges file that doesn't parse is never written (Load → null)", BrainXBridges.Load(file) == null);
            File.WriteAllText(file, "// notes\n{ \"version\": 1, \"bridges\": [], }");
            Check("C17", "comments and a trailing comma are read", BrainXBridges.Load(file) is { } withComments && withComments["bridges"] is JsonArray);
            Check("C18", "a missing file starts empty (version 1)", BrainXBridges.Load(Path.Combine(dir, "none.json")) is { } empty && (int)empty["version"]! == 1);

            string fakeLocal = Path.Combine(dir, "local");
            Directory.CreateDirectory(Path.Combine(fakeLocal, "BrainX"));
            File.WriteAllText(Path.Combine(fakeLocal, "BrainX", "vault-path.txt"), dir + "\r\n");
            Check("C19", "the vault is found where BrainX points (vault-path.txt), not guessed", BrainXBridges.ResolveVault(null, fakeLocal) == Path.GetFullPath(dir));
            Check("C20", "no BrainX pointer and no BRAINX_VAULT folder → no vault (the connection stays in CluadeX)",
                BrainXBridges.ResolveVault(Path.Combine(dir, "nope"), Path.Combine(dir, "empty")) is null || Environment.GetEnvironmentVariable("BRAINX_VAULT") != null);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine("── connections: Setup page rows ──");
        var rows = FakeConnections().Select(SystemSetupService.ConnectionRow).ToList();
        Check("C21", "connected through BrainX → ready, 'shared by every agent', a Disconnect button",
            rows[0] is { State: SetupState.Ready, Detail: "place:brainx", Secondary.Kind: SetupActionKind.Disconnect, Group: SystemSetupService.GroupConnect });
        Check("C22", "not connected → Connect (Stripe needs a key), and it says it will go to BrainX",
            rows[1] is { State: SetupState.NeedsKey, Detail: "place:willBrainx", Primary.Kind: SetupActionKind.Connect, Secondary.Kind: SetupActionKind.Guide });
        Check("C23", "set up by hand already → ready, no second connection offered, no Disconnect of the user's entry",
            rows[2] is { State: SetupState.Ready, Detail: "place:manualCluadex", Secondary: null } && rows[2].Primary?.Kind == SetupActionKind.Guide);
        Check("C24", "connected but not live yet → says the tools come after BrainX restarts",
            SystemSetupService.ConnectionRow(FakeConnections()[0] with { Live = false }).Detail == "place:brainx|notLive");
        Check("C25", "no BrainX → it will be added to CluadeX", SystemSetupService.ConnectionRow(FakeConnections()[1] with { BrainAvailable = false }).Detail == "place:willCluadex");

        Console.WriteLine("── connections: keys are encrypted on this PC ──");
        string sdir = Path.Combine(Path.GetTempPath(), "cx-secrets-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var secrets = new ConnectorSecrets(sdir);
            secrets.Set("stripe", "rk_test_TOPSECRET_123456789");
            string raw = File.ReadAllText(secrets.FilePath);
            Check("C26", "stored DPAPI-encrypted (the key's text is not in the file) and read back", !raw.Contains("TOPSECRET") && secrets.Get("stripe") == "rk_test_TOPSECRET_123456789");
            File.WriteAllText(secrets.FilePath, "{\"stripe\":\"bm90LWEtZHBhcGktYmxvYg==\"}");
            Check("C27", "a blob from another PC / account reads as 'no key' (asks again), never throws", secrets.Get("stripe") == null);
            secrets.Set("stripe", "rk_test_again_123456789000");
            secrets.Remove("stripe");
            Check("C28", "disconnect removes the key", !secrets.Has("stripe"));
        }
        finally { try { Directory.Delete(sdir, true); } catch { } }

        Console.WriteLine("── GitHub server download: checksums ──");
        const string sums = "aaaa  other.zip\n" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789ABCDEF  github-mcp-server_Windows_x86_64.zip\n";
        Check("C29", "the zip's hash is read from GitHub's checksums file (case-insensitive), a missing one is null",
            ConnectionService.ChecksumFor(sums, ConnectorCatalog.GitHubServerAsset) == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            && ConnectionService.ChecksumFor(sums, "nope.zip") == null && ConnectionService.ChecksumFor("bad line", ConnectorCatalog.GitHubServerAsset) == null);

        Console.WriteLine("── one-click BrainX: what is refused ──");
        Check("C30", "only https from xman4289.com, never a web page or JSON, only an installer-sized file",
            BrainXInstaller.RefuseReason(new Uri("https://xman4289.com/brainx/download"), "application/octet-stream", 270_000_000) == null
            && BrainXInstaller.RefuseReason(new Uri("https://github.com/x/releases/a.exe"), "application/octet-stream", 270_000_000) == "brainx.err.host"
            && BrainXInstaller.RefuseReason(new Uri("http://xman4289.com/a"), null, null) == "brainx.err.host"
            && BrainXInstaller.RefuseReason(new Uri("https://xman4289.com.evil.io/a"), null, null) == "brainx.err.host"
            && BrainXInstaller.RefuseReason(new Uri("https://xman4289.com/products/brainx-cloud"), "text/html", 143_000) == "brainx.err.notFile"
            && BrainXInstaller.RefuseReason(new Uri("https://xman4289.com/a"), "application/octet-stream", 1000) == "brainx.err.size");

        static Func<HttpMessageHandler> Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) => () => new FakeHandler(answer);
        var redirectAway = new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/BrainX-win-Setup.exe") } }));
        Check("C31", "a redirect off xman4289.com is stopped before anything is downloaded", await Throws(() => redirectAway.DownloadAsync()) == "brainx.err.host");
        var toPage = new BrainXInstaller(handler: Fake(r => r.RequestUri!.AbsolutePath == "/brainx/download"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/products/brainx-cloud", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html") }));
        Check("C32", "today's answer (302 to the product page) = Coming Soon: said as such, not an error, no page opened",
            await Throws(() => toPage.DownloadAsync()) == BrainXInstaller.ComingSoon);
        var forbidden = new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"message\":\"Coming soon\", \"coming_soon\": true}", Encoding.UTF8, "application/json") }));
        Check("C37", "the store's JSON answer (403 {\"coming_soon\": true}) is Coming Soon too; another 403 is a server error",
            await Throws(() => forbidden.DownloadAsync()) == BrainXInstaller.ComingSoon
            && await Throws(() => new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.Forbidden))).DownloadAsync()) == "brainx.err.http");
        var otherHtml = new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") }));
        Check("C38", "an HTML page that isn't a product page is still 'not the installer' (the page opens)", await Throws(() => otherHtml.DownloadAsync()) == "brainx.err.notFile");
        byte[] notExe = new byte[6 * 1024 * 1024];
        var fakeZip = new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(notExe) }));
        Check("C33", "a file of the right size that isn't an exe (no MZ) is deleted, not run", await Throws(() => fakeZip.DownloadAsync()) == "brainx.err.notFile");
        byte[] exe = new byte[6 * 1024 * 1024]; exe[0] = (byte)'M'; exe[1] = (byte)'Z';
        var good = new BrainXInstaller(handler: Fake(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) }));
        string? got = null;
        try { got = await good.DownloadAsync(); } catch { }
        Check("C34", "a real-looking installer is downloaded (to its own temp folder)", got != null && File.Exists(got) && BrainXInstaller.IsExe(got));
        Check("C35", "Authenticode: an unsigned exe is 'Unsigned' (shown to the user with its SHA-256), a signed Windows exe is 'Valid'",
            got != null && BrainXInstaller.VerifySignature(got) == SignatureCheck.Unsigned
            && BrainXInstaller.VerifySignature(Path.Combine(Environment.SystemDirectory, "notepad.exe")) is SignatureCheck.Valid or SignatureCheck.Unsigned);
        if (got != null) BrainXInstaller.TryDelete(got);

        Console.WriteLine("── live (read-only): GitHub's release has the Windows server and its checksum ──");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-setuptest");
            var rel = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{ConnectorCatalog.GitHubServerRepo}/releases/latest"));
            var names = rel.RootElement.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
            Check("C36", $"latest {rel.RootElement.GetProperty("tag_name").GetString()} carries {ConnectorCatalog.GitHubServerAsset} and a checksums file",
                names.Contains(ConnectorCatalog.GitHubServerAsset) && names.Any(n => n!.EndsWith("_checksums.txt")), string.Join(", ", names));
        }
        catch (Exception ex) { Console.WriteLine($"  SKIP C36  no network to api.github.com ({ex.GetType().Name})"); }
    }

    static async Task<string?> Throws(Func<Task> f)
    {
        try { await f(); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = _answer(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }
}
