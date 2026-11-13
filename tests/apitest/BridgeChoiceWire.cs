// Which browser the agent's tools work in when both Chrome and Edge have the CluadeX Bridge extension (2026-11-13: with
// Chrome connected there was no way to get Edge in). The rule is pure; the server half runs on a real named pipe with a
// name of its own, so the owner's connection (cluadex-chrome-<sid>) is never touched. No browser, no registry.
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Chrome;

namespace ApiTest;

internal static class BridgeChoiceWire
{
    const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36";
    const string EdgeUa = ChromeUa + " Edg/157.0.0.0";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · the browser the agent uses when Chrome and Edge both have the extension");
        var t0 = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
        var ends = t0 + BridgeBrowserChoice.Window;

        // ─── the rule ───
        check("bridge-choice", "no browser chosen: whoever says hello is let in (as before)",
            BridgeBrowserChoice.Accept(BridgeBrowserChoice.Any, ChromeUa, t0, ends, 0) && BridgeBrowserChoice.Accept(BridgeBrowserChoice.Any, EdgeUa, t0, ends, 0), "");
        check("bridge-choice", "Edge chosen: Edge is let in at once, Chrome is turned away while the window is open",
            BridgeBrowserChoice.Accept(BridgeBrowserChoice.Edge, EdgeUa, t0, ends, 0) && !BridgeBrowserChoice.Accept(BridgeBrowserChoice.Edge, ChromeUa, t0, ends, 0)
            && !BridgeBrowserChoice.Accept(BridgeBrowserChoice.Edge, ChromeUa, t0.AddSeconds(19), ends, 3), "");
        check("bridge-choice", "…and Chrome is let in once the window is over, or after four were turned away: the tools must not stay without a browser",
            BridgeBrowserChoice.Accept(BridgeBrowserChoice.Edge, ChromeUa, ends, ends, 0)
            && BridgeBrowserChoice.Accept(BridgeBrowserChoice.Edge, ChromeUa, t0, ends, BridgeBrowserChoice.MaxRefusals), "");
        check("bridge-choice", "Chrome chosen: the same the other way round",
            BridgeBrowserChoice.Accept(BridgeBrowserChoice.Chrome, ChromeUa, t0, ends, 0) && !BridgeBrowserChoice.Accept(BridgeBrowserChoice.Chrome, EdgeUa, t0, ends, 0), "");
        check("bridge-choice", "the setting is read loosely (case, spaces); an unknown value means the first to connect",
            BridgeBrowserChoice.Normalize(" Edge ") == BridgeBrowserChoice.Edge && BridgeBrowserChoice.Normalize("CHROME") == BridgeBrowserChoice.Chrome
            && BridgeBrowserChoice.Normalize("firefox") == BridgeBrowserChoice.Any && BridgeBrowserChoice.Normalize(null) == BridgeBrowserChoice.Any
            && BridgeBrowserChoice.Of(EdgeUa) == BridgeBrowserChoice.Edge && BridgeBrowserChoice.Of(ChromeUa) == BridgeBrowserChoice.Chrome
            && BridgeBrowserChoice.Of(null) == BridgeBrowserChoice.Chrome, "");

        // ─── the server, on a pipe of its own ───
        string pipeName = "cluadex-apitest-bridge-" + Guid.NewGuid().ToString("N")[..10];
        using var server = new ChromeBridgeServer(pipeName);
        server.Start();
        try
        {
            // 1. nobody chooses: a hello is welcomed
            await using (var chrome = await ConnectAsync(pipeName))
            {
                await SendHelloAsync(chrome, ChromeUa);
                string? first = await ReadFrameAsync(chrome, 5000);
                bool up = await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                check("bridge-server", "no choice: a hello gets its welcome and the connection counts",
                    first != null && first.Contains("\"welcome\"") && up && BridgeBrowserChoice.Of(server.UserAgent) == BridgeBrowserChoice.Chrome,
                    $"frame: {first ?? "null"} · connected {up}");

                // 2. the owner picks the other browser: this one lets go, and the server listens again
                server.DropConnection();
                bool closed = await ReadFrameAsync(chrome, 5000) == null;
                await WaitUntilAsync(() => !server.IsConnected, 5000);
                check("bridge-server", "DropConnection ends that one connection (the client sees the pipe close) and nothing is connected",
                    closed && !server.IsConnected, $"closed {closed} · connected {server.IsConnected}");
            }

            // 3. Edge is wanted: Chrome's hello is turned away with no welcome, Edge's is taken
            server.AcceptBrowser = ua => BridgeBrowserChoice.Of(ua) == BridgeBrowserChoice.Edge;
            int refusedBefore = server.Refused;
            await using (var chrome = await ConnectAsync(pipeName))
            {
                await SendHelloAsync(chrome, ChromeUa);
                string? answer = await ReadFrameAsync(chrome, 5000);
                check("bridge-server", "a browser that is not the wanted one is turned away: no welcome, the pipe closes, it never counted as connected",
                    answer == null && !server.IsConnected && server.Refused == refusedBefore + 1, $"frame: {answer ?? "null"} · refused {server.Refused - refusedBefore}");
            }
            await using (var edge = await ConnectAsync(pipeName))
            {
                await SendHelloAsync(edge, EdgeUa);
                string? welcome = await ReadFrameAsync(edge, 5000);
                bool up = await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                check("bridge-server", "the server listened again after turning one away, and the wanted browser gets in",
                    welcome != null && welcome.Contains("\"welcome\"") && up && BridgeBrowserChoice.Of(server.UserAgent) == BridgeBrowserChoice.Edge,
                    $"frame: {welcome ?? "null"} · connected {up}");
            }
            await WaitUntilAsync(() => !server.IsConnected, 5000);

            // 4. the connector: the owner's choice is kept, the other browser is let go, and only the chosen one comes in
            var settings = new SettingsService();   // portable — never the user's settings
            string engineBefore = settings.Settings.BrowserEngine, choiceBefore = settings.Settings.BridgeBrowser;
            try
            {
                settings.UpdateSettings(s => { s.BrowserEngine = BrowserService.ChromeEngine; s.BridgeBrowser = ""; });
                var connector = new ChromeBridgeConnector(settings, server);   // (sets the server's AcceptBrowser to its own rule)
                await using var chrome = await ConnectAsync(pipeName);
                await SendHelloAsync(chrome, ChromeUa);
                await ReadFrameAsync(chrome, 5000);
                await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                check("bridge-connector", "nothing chosen: Chrome connects, and it is the browser in use",
                    connector.Connected == BridgeBrowserChoice.Chrome && connector.Preferred == BridgeBrowserChoice.Any && !connector.PreferredNotConnected, connector.Connected);

                connector.Use("edge");
                bool letGo = await ReadUntilClosedAsync(chrome, 5000);
                await WaitUntilAsync(() => !server.IsConnected, 5000);
                check("bridge-connector", "\"let the agent use Edge\": the choice is saved and Chrome lets go at once",
                    settings.Settings.BridgeBrowser == "edge" && connector.Preferred == BridgeBrowserChoice.Edge && letGo && connector.Connected == "",
                    $"saved '{settings.Settings.BridgeBrowser}' · let go {letGo} · connected '{connector.Connected}'");

                await using (var chromeAgain = await ConnectAsync(pipeName))
                {
                    await SendHelloAsync(chromeAgain, ChromeUa);
                    bool turnedAway = await ReadFrameAsync(chromeAgain, 5000) == null;
                    check("bridge-connector", "Chrome trying again a second later is turned away (the window is open)", turnedAway && !server.IsConnected, $"turned away {turnedAway}");
                }
                await using (var edge = await ConnectAsync(pipeName))
                {
                    await SendHelloAsync(edge, EdgeUa);
                    string? welcome = await ReadFrameAsync(edge, 5000);
                    await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    check("bridge-connector", "Edge, whose host was waiting, gets in and is the browser in use",
                        welcome != null && connector.Connected == BridgeBrowserChoice.Edge && connector.ConnectedIsEdge && !connector.PreferredNotConnected, connector.Connected);
                }
                await WaitUntilAsync(() => !server.IsConnected, 5000);

                // the chosen browser never comes: after four are turned away the other one is let in, and the row can say so
                bool chromeIn = false;
                for (int i = 0; i < BridgeBrowserChoice.MaxRefusals + 2 && !chromeIn; i++)
                {
                    await using var retry = await ConnectAsync(pipeName);
                    await SendHelloAsync(retry, ChromeUa);
                    if (await ReadFrameAsync(retry, 5000) is { } w && w.Contains("\"welcome\""))
                    {
                        await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                        chromeIn = connector.Connected == BridgeBrowserChoice.Chrome && connector.PreferredNotConnected;
                    }
                }
                check("bridge-connector", "Edge chosen but it never comes: Chrome is let in after a few tries, and the connector knows the chosen one is missing",
                    chromeIn, $"chrome in {chromeIn} · refused {server.Refused}");
                await WaitUntilAsync(() => !server.IsConnected, 5000);

                connector.Use("");
                check("bridge-connector", "back to \"the first to connect\": saved, and nobody is turned away any more",
                    settings.Settings.BridgeBrowser == "" && connector.Preferred == BridgeBrowserChoice.Any, settings.Settings.BridgeBrowser);

                // 5. the agent's own tools on this connection: browser_status says which browser, browser_use switches it
                // (owner 2026-11-13: "ใช้งาน Edge หรือ Chrome ผ่าน extension ตัวเองได้อิสระตามสั่ง และรู้ว่าใช้บราวเซอร์อะไรอยู่").
                // The engine stays "chrome" throughout: switching the connection on or off writes the real host
                // registration, which a harness must never do — so target "builtin" is not run here.
                string T(string key) => LocalizedResources.Instance[key];
                var module = new BrowserToolModule(new BrowserService(settings), settings, null, null, null, null, null, _ => null, connector);
                var ctx = new CluadeX.Services.Tools.ToolModuleContext { SessionKey = "bridge-tools" };
                CluadeX.Models.ToolCall Call(CluadeX.Models.ToolType type, string name, string? target = null) => new()
                {
                    Type = type, ToolName = name, Arguments = target == null ? new() : new() { ["target"] = target },
                };
                var waitBefore = BrowserToolModule.UseWait;
                var started = new List<string>();
                module.StartBrowserOverride = b => { started.Add(b); return true; };   // never a real browser
                BrowserToolModule.UseWait = TimeSpan.FromSeconds(3);
                try
                {
                    var none = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserStatus, "browser_status"), ctx, CancellationToken.None);
                    check("bridge-tools", "browser_status with nothing connected: says so, names the user's choice and how to switch",
                        none.Success && none.Output.Contains(T("browser.status.ownNone"))
                        && none.Output.Contains(string.Format(T("browser.status.chosen"), T("browser.status.chosenAny"))) && none.Output.Contains("browser_use"), none.Output);

                    await using (var edge = await ConnectAsync(pipeName))
                    {
                        await SendHelloAsync(edge, EdgeUa);
                        await ReadFrameAsync(edge, 5000);
                        await server.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                        var onEdge = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserStatus, "browser_status"), ctx, CancellationToken.None);
                        check("bridge-tools", "browser_status with Edge connected: names Microsoft Edge and CluadeX's own extension",
                            onEdge.Success && onEdge.Output.Contains(string.Format(T("browser.status.own"), "Microsoft Edge", ChromeBridgeInstall.BundledVersion)), onEdge.Output);

                        // the user says "use Chrome": Edge lets go, Chrome (its host was waiting) gets in, the tool answers with it
                        var chromeComes = Task.Run(async () =>
                        {
                            await ReadUntilClosedAsync(edge, 5000);
                            var c = await ConnectAsync(pipeName);
                            await SendHelloAsync(c, ChromeUa);
                            await ReadFrameAsync(c, 5000);
                            return c;
                        });
                        var toChrome = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "chrome"), ctx, CancellationToken.None);
                        await using var chromeNow = await chromeComes;
                        check("bridge-tools", "browser_use target=chrome while Edge is connected: the choice is kept, Edge lets go, and the answer names Google Chrome",
                            toChrome.Success && toChrome.Output.Contains("Google Chrome") && settings.Settings.BridgeBrowser == "chrome"
                            && connector.Connected == BridgeBrowserChoice.Chrome, $"{toChrome.Success} · {toChrome.Output}{toChrome.Error} · connected '{connector.Connected}'");

                        var any = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "any"), ctx, CancellationToken.None);
                        check("bridge-tools", "browser_use target=any with a browser connected: it stays, at once, and the choice goes back to \"the first to connect\"",
                            any.Success && any.Output.Contains("Google Chrome") && settings.Settings.BridgeBrowser == "" && connector.Connected == BridgeBrowserChoice.Chrome, any.Output);

                        // the user says "use Edge" but Edge never comes
                        started.Clear();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var noEdge = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "edge"), ctx, CancellationToken.None);
                        string edgeDetail = $"{noEdge.Success} · {noEdge.Output}{noEdge.Error} · {sw.ElapsedMilliseconds} ms · started [{string.Join(",", started)}]";
                        if (connector.EdgeFound)
                            check("bridge-tools", "browser_use target=edge when Edge's extension never connects: a failure that says why and how to fix it, after the wait — no real browser is started by the harness",
                                !noEdge.Success && (noEdge.Error ?? noEdge.Output).Contains("Microsoft Edge") && (noEdge.Error ?? noEdge.Output).Contains("browser_status")
                                && sw.ElapsedMilliseconds >= 2500 && sw.ElapsedMilliseconds < 15000 && settings.Settings.BridgeBrowser == "edge", edgeDetail);
                        else
                            check("bridge-tools", "browser_use target=edge on a PC without Edge: refused at once", !noEdge.Success && sw.ElapsedMilliseconds < 2000, edgeDetail);
                    }
                    await WaitUntilAsync(() => !server.IsConnected, 5000);

                    var bad = await module.ExecuteAsync(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "firefox"), ctx, CancellationToken.None);
                    check("bridge-tools", "browser_use with a browser CluadeX cannot drive is refused with the choices", !bad.Success && (bad.Error ?? bad.Output).Contains("builtin"), bad.Error ?? bad.Output);

                    // permission: free between the user's own browsers and back to the pane; from the pane to the user's own
                    // browser asks once per chat, whatever the permission switch says
                    var free = module.DescribePermission(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "edge"), ctx);
                    var statusPerm = module.DescribePermission(Call(CluadeX.Models.ToolType.BrowserStatus, "browser_status"), ctx);
                    settings.UpdateSettings(s => s.BrowserEngine = "auto");
                    var asks = module.DescribePermission(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "edge"), ctx);
                    var back = module.DescribePermission(Call(CluadeX.Models.ToolType.BrowserUse, "browser_use", "builtin"), ctx);
                    settings.UpdateSettings(s => s.BrowserEngine = BrowserService.ChromeEngine);
                    check("bridge-tools", "permission: status is free; a switch between the user's own browsers is free; from the pane to the user's browser asks once per chat even with permissions off; back to the pane is free",
                        statusPerm.IsFree && free.IsFree && !asks.IsFree && asks.AskEvenWhenPermissionsOff && asks.OnceKey == "browser-use-own"
                        && (asks.Detail ?? "").Contains("Microsoft Edge") && back.IsFree, $"free={free.IsFree} asks={!asks.IsFree}/{asks.AskEvenWhenPermissionsOff}/{asks.OnceKey} back={back.IsFree}");

                    // every agent hears about them: the native schemas, the names a CLI is told, the aliases
                    var schemaNames = BrowserToolModule.Schemas().Select(s => s.Name).ToList();
                    string hint = CluadeX.Services.Tools.ChatToolBridge.BrowserHint(null, claudeNames: true);
                    string down = CluadeX.Services.Tools.ChatToolBridge.BrowserHint("Dev Preview is off");
                    check("bridge-tools", "both tools are in the schemas and in what a CLI agent is told; aliases resolve",
                        schemaNames.Contains("browser_status") && schemaNames.Contains("browser_use")
                        && hint.Contains("mcp__cluadex__browser_status") && hint.Contains("mcp__cluadex__browser_use") && hint.Contains("target=edge|chrome|builtin")
                        && BrowserToolModule.TypeOf("browser_switch") == CluadeX.Models.ToolType.BrowserUse && BrowserToolModule.TypeOf("BROWSER_WHICH") == CluadeX.Models.ToolType.BrowserStatus, "");
                    check("bridge-tools", "the order of browser routes: CluadeX's own first; Claude in Chrome when the user names it, or when CluadeX's own cannot be used",
                        hint.Contains("before any other browser tool") && hint.Contains("unless the user names one") && hint.Contains("only then may another browser tool")
                        && down.Contains("unavailable") && down.Contains("may then be used") && !down.Contains("do not switch") && !hint.Contains('\n') && !down.Contains('\n'), down);
                    string promptText = module.GetPromptSection(new CluadeX.Services.Tools.PromptSectionContext { OfferedToolNames = Array.Empty<string>() }) ?? "";
                    check("bridge-tools", "the native loop's prompt says the same: browser_status / browser_use, and when another route may be used",
                        promptText.Contains("browser_status") && promptText.Contains("browser_use target=edge|chrome|builtin") && promptText.Contains("Claude in Chrome")
                        && promptText.Length <= 1400,   // browsertest G2c's ceiling: the section rides on every request
                        $"{promptText.Length} chars");
                }
                finally
                {
                    BrowserToolModule.UseWait = waitBefore;
                    module.StartBrowserOverride = null;
                }
            }
            finally { settings.UpdateSettings(s => { s.BrowserEngine = engineBefore; s.BridgeBrowser = choiceBefore; }); }
        }
        finally { server.Stop(); }
    }

    static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // the server makes a new pipe instance after each connection ends: give it a moment
        for (int i = 0; ; i++)
        {
            try { await pipe.ConnectAsync(1000); return pipe; }
            catch (Exception ex) when ((ex is TimeoutException or IOException) && i < 8) { await Task.Delay(150); }
        }
    }

    static async Task SendHelloAsync(Stream pipe, string userAgent)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { ev = "hello", version = ChromeBridgeInstall.BundledVersion, userAgent, paused = false });
        byte[] frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame, body.Length);
        body.CopyTo(frame, 4);
        await pipe.WriteAsync(frame);
        await pipe.FlushAsync();
    }

    /// <summary>One frame from the app as text, or null when the pipe closed (or nothing came in time).</summary>
    static async Task<string?> ReadFrameAsync(Stream pipe, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var header = new byte[4];
            await pipe.ReadExactlyAsync(header, cts.Token);
            var body = new byte[BitConverter.ToInt32(header)];
            await pipe.ReadExactlyAsync(body, cts.Token);
            return Encoding.UTF8.GetString(body);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException) { return null; }
    }

    /// <summary>Read (and drop) what the app still sends until the pipe closes. True when it closed in time.</summary>
    static async Task<bool> ReadUntilClosedAsync(Stream pipe, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[4096];
        try
        {
            while (await pipe.ReadAsync(buffer, cts.Token) > 0) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    static async Task WaitUntilAsync(Func<bool> done, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(50);
    }
}
