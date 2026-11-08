using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Account;

namespace ApiTest;

/// <summary>
/// The Xman Studio account sign-in and the connection doctor (installer contract 1, 2.2–2.5) without touching xman4289.com:
/// a localhost copy of the app-auth/* routes plus /activate. Proves the "server not upgraded yet" path first (health 404 →
/// NotDeployed, not an error), every envelope rule (5xx, 429, a captive page, Laravel's own 404), password login with each
/// error code, the device code (pending → approved once → expired, a QR that only ever points at xman4289.com),
/// free-license, a bought key that is active on another PC (asked, and force_rebind only after a yes), the account state
/// that is saved (never a password or a code), the error-code table, and the doctor's verdicts and masked report.
///
///   dotnet run -- --account
/// </summary>
internal static class AccountWire
{
    /// <summary>"" = app-auth routes missing (the server today); "up" = deployed.</summary>
    static string Mode = "";
    /// <summary>Answer every call this way: "" | "503" | "429" | "captive" | "laravel404".</summary>
    static string Fault = "";
    static readonly List<(string Path, string Body)> Calls = new();
    static readonly Dictionary<string, string> Devices = new();   // device_code → pending|approved|consumed|denied
    static string BoundMachine = "other-pc";                      // where the bought key is active
    static bool FreeAllowed = true;

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · account sign-in + connection doctor (localhost mock of app-auth/*)");
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", $"http://localhost:{port}/api/v1/product/cluadex-ai-coding-assistant");

        var settings = new SettingsService();   // portable — never the user's settings
        settings.UpdateSettings(s =>
        {
            s.ActivationKey = null; s.LicenseType = null; s.LicenseExpiresUtc = null; s.LicenseValidatedUtc = null;
            s.AccountLinked = false; s.AccountEmailMasked = null; s.AccountName = null; s.AccountLinkedUtc = null;
        });
        var license = new XmanLicenseService(settings);
        var activation = new ActivationService(settings, license);
        var account = new XmanAccountService(settings, license, activation);
        var loc = new LocalizationService();
        loc.SetLanguage("th");

        // ── 1. the server today: app-auth is not deployed ──
        Mode = "";
        var h = await account.HealthAsync();
        check("account", "health 404 (route missing) = NotDeployed, not an error", h.Availability == AppAuthAvailability.NotDeployed && h.ErrorCode == "NOT_DEPLOYED", $"{h.Availability} {h.ErrorCode} {h.HttpStatus}");
        check("account", "404 still gives the server's clock (Date header)", h.ServerTimeUtc is { } st && Math.Abs((st - DateTime.UtcNow).TotalMinutes) < 2, $"{h.ServerTimeUtc:u}");
        Fault = "laravel404";
        h = await account.HealthAsync();
        check("account", "Laravel's JSON 404 without error_code = NotDeployed", h.Availability == AppAuthAvailability.NotDeployed, $"{h.Availability} {h.ErrorCode}");
        Fault = "";
        var notDeployedLogin = await account.LoginAsync("a@b.c", "x");
        check("account", "login while not deployed → NOT_DEPLOYED (Thai sentence, no raw text)",
            !notDeployedLogin.Success && notDeployedLogin.ErrorCode == "NOT_DEPLOYED"
            && AccountErrors.Describe(notDeployedLogin.ErrorCode, loc) == "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป", AccountErrors.Describe(notDeployedLogin.ErrorCode, loc));

        // ── 2. envelope rules ──
        Mode = "up";
        foreach (var (fault, expect) in new[] { ("503", "SERVER_ERROR"), ("429", "RATE_LIMIT_EXCEEDED"), ("captive", "NOT_JSON") })
        {
            Fault = fault;
            h = await account.HealthAsync();
            check("account", $"health under {fault} → Unreachable {expect}", h.Availability == AppAuthAvailability.Unreachable && h.ErrorCode == expect, $"{h.Availability} {h.ErrorCode}");
        }
        Fault = "";
        h = await account.HealthAsync();
        check("account", "health deployed → Ready with capabilities", h.Availability == AppAuthAvailability.Ready && h.PasswordLogin && h.DeviceLogin && h.FreeLicense, $"{h}");

        // ── 3. password login ──
        int before = Calls.Count;
        var empty = await account.LoginAsync("  ", "x");
        check("account", "empty login: local Thai error, nothing sent", empty.ErrorCode == "EMPTY_LOGIN" && Calls.Count == before
            && AccountErrors.Describe(empty.ErrorCode, loc) == "กรอกอีเมลหรือไอดี Xman Studio", empty.ErrorCode);
        var noPass = await account.LoginAsync("a@b.c", "");
        check("account", "empty password: local error, nothing sent", noPass.ErrorCode == "EMPTY_PASSWORD" && Calls.Count == before, noPass.ErrorCode);
        foreach (var (login, wantCode) in new[] { ("wrong@x.com", "INVALID_CREDENTIALS"), ("disabled@x.com", "ACCOUNT_DISABLED"), ("flood@x.com", "RATE_LIMIT_EXCEEDED"), ("weird@x.com", "SOMETHING_NEW") })
        {
            var r = await account.LoginAsync(login, "pw");
            check("account", $"login {login} → {wantCode}", !r.Success && r.ErrorCode == wantCode, $"{r.ErrorCode} HTTP {r.HttpStatus}");
        }
        check("account", "unknown code reads 'เซิร์ฟเวอร์ตอบว่า <code>'", AccountErrors.Describe("SOMETHING_NEW", loc) == "เซิร์ฟเวอร์ตอบว่า SOMETHING_NEW", AccountErrors.Describe("SOMETHING_NEW", loc));
        check("account", "the failure is remembered for the report", account.LastError is { Code: "SOMETHING_NEW", Endpoint: "/app-auth/login" }, $"{account.LastError}");
        var lastLogin = LastBody("/app-auth/login");
        check("account", "login sends login + password + machine fields", Str(lastLogin, "login") == "weird@x.com" && Str(lastLogin, "machine_id")?.Length == 64
            && !string.IsNullOrEmpty(Str(lastLogin, "app_version")), lastLogin.ToString());

        var ok = await account.LoginAsync("buyer@x.com", "s3cret-pw");
        check("account", "login ok → LinkResult with masked e-mail and a bought key", ok.Success && ok.Link is { AccountLinked: true, EmailMasked: "bu***@x.com", License: { IsPaid: true, Type: "yearly" } },
            $"{ok.Link}");

        // ── 4. a bought key active on another PC: asked; force only after yes ──
        int asked = 0;
        var declined = await account.ApplyAsync(ok.Link!, () => { asked++; return Task.FromResult(false); });
        check("account", "key on another PC: the user is asked", asked == 1 && declined.Status == LinkApplyStatus.MoveDeclined, $"{declined.Status} asked={asked}");
        check("account", "declined: no force_rebind was sent, nothing activated here", !Calls.Any(c => c.Path.EndsWith("/activate") && c.Body.Contains("force_rebind")) && !activation.IsActivated, "");
        check("account", "the account is remembered anyway (masked e-mail, name, time — no password)",
            settings.Settings.AccountLinked && settings.Settings.AccountEmailMasked == "bu***@x.com" && settings.Settings.AccountLinkedUtc != null, "");
        string settingsJson = File.ReadAllText(Path.Combine(settings.DataRoot, "settings.json"));
        check("account", "settings.json holds no password and no device code", !settingsJson.Contains("s3cret-pw") && !settingsJson.Contains("devsecret"), "");
        var moved = await account.ApplyAsync(ok.Link!, () => Task.FromResult(true));
        check("account", "yes → /activate with force_rebind → Pro here", moved.Status == LinkApplyStatus.Activated && activation.IsActivated && activation.ActivationTier == "pro"
            && Calls.Any(c => c.Path.EndsWith("/activate") && c.Body.Contains("\"force_rebind\":\"true\"")), $"{moved.Status} {activation.ActivationTier}");

        // ── 5. account without a license ──
        var bare = await account.LoginAsync("nolicense@x.com", "pw");
        var bareApplied = await account.ApplyAsync(bare.Link!, () => Task.FromResult(false));
        check("account", "account with no license → LinkedNoLicense + store link on xman4289.com", bareApplied.Status == LinkApplyStatus.LinkedNoLicense
            && bareApplied.PurchaseUrl.StartsWith("https://xman4289.com/"), $"{bareApplied.Status} {bareApplied.PurchaseUrl}");

        // ── 6. device code (QR) ──
        var (code, err, _, _) = await account.StartDeviceAsync();
        check("account", "device/start → a code with a user code and the QR link", code != null && code.UserCode == "KQ7M-4TZP" && code.IntervalSeconds == 3
            && code.VerificationUriComplete.Contains("code=KQ7M-4TZP"), $"{code} {err}");
        check("account", "the device secret never shows in ToString (logs)", code != null && !code.ToString().Contains(code.Secret), code?.ToString() ?? "");
        var p1 = await account.PollDeviceAsync(code!);
        check("account", "poll before approval → Pending", p1.State == DevicePollState.Pending, $"{p1.State} {p1.ErrorCode}");
        lock (Devices) Devices[code!.Secret] = "slow";
        var pSlow = await account.PollDeviceAsync(code);
        check("account", "SLOW_DOWN understood", pSlow.State == DevicePollState.SlowDown, $"{pSlow.State}");
        lock (Devices) Devices[code.Secret] = "approved";
        var p2 = await account.PollDeviceAsync(code);
        check("account", "approved → LinkResult", p2.State == DevicePollState.Approved && p2.Link is { AccountLinked: true }, $"{p2.State}");
        var p3 = await account.PollDeviceAsync(code);
        check("account", "the code works once (then EXPIRED_TOKEN)", p3.State == DevicePollState.Expired && p3.ErrorCode == "EXPIRED_TOKEN", $"{p3.State}");
        lock (Devices) Devices[code.Secret] = "denied";
        var p4 = await account.PollDeviceAsync(code);
        check("account", "denied on the phone → ACCESS_DENIED", p4.State == DevicePollState.Denied && AccountErrors.Describe(p4.ErrorCode, loc) == "ยกเลิกการเชื่อมจากมือถือแล้ว", $"{p4.State} {p4.ErrorCode}");
        check("account", "QR link: only https on xman4289.com", XmanAccountService.SafeVerificationUri("https://xman4289.com/device?code=A") != null
            && XmanAccountService.SafeVerificationUri("https://evil.example/device") == null && XmanAccountService.SafeVerificationUri("http://xman4289.com/device") == null
            && XmanAccountService.SafeVerificationUri("https://xman4289.com.evil.example/device") == null, "");

        // ── 7. free license (the quick path) ──
        settings.UpdateSettings(s => { s.ActivationKey = null; s.LicenseType = null; s.LicenseExpiresUtc = null; });
        var activation2 = new ActivationService(settings, license);
        var account2 = new XmanAccountService(settings, license, activation2);
        var free = await account2.FreeLicenseAsync();
        check("account", "free-license → a FREE key", free.Success && free.Link?.License is { Source: "free", IsPaid: false }, $"{free.Link?.License}");
        var freeApplied = await account2.ApplyAsync(free.Link!, () => Task.FromResult(false));
        check("account", "the free key is taken as is (no /activate), tier stays free", freeApplied.Status == LinkApplyStatus.Activated
            && settings.Settings.ActivationKey?.StartsWith("FREE-") == true && !activation2.IsActivated, $"{settings.Settings.LicenseType}");
        FreeAllowed = false;
        var refused = await account2.FreeLicenseAsync();
        check("account", "free license closed → FREE_LICENSE_NOT_AVAILABLE in Thai", refused.ErrorCode == "FREE_LICENSE_NOT_AVAILABLE"
            && AccountErrors.Describe(refused.ErrorCode, loc) == "ไลเซ่นฟรียังไม่เปิดสำหรับโปรแกรมนี้", refused.ErrorCode);
        FreeAllowed = true;

        // ── 8. offline: no server at all ──
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", "http://127.0.0.1:1/api/v1/product/cluadex-ai-coding-assistant");
        var deadAccount = new XmanAccountService(settings, new XmanLicenseService(settings), activation2);
        var dead = await deadAccount.LoginAsync("a@b.c", "pw");
        check("account", "no server → OFFLINE (the doctor runs), never an exception", dead.ErrorCode == "OFFLINE" && AccountErrors.IsTransport(dead.ErrorCode), $"{dead.ErrorCode} {dead.TechDetail}");
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", $"http://localhost:{port}/api/v1/product/cluadex-ai-coding-assistant");

        // ── 9. the doctor ──
        section("offline · connection doctor: verdicts and the masked report");
        check("doctor", "api: NotDeployed → Warn 'ยังไม่รองรับ'", ConnectionDoctor.ClassifyApi(new AppAuthHealth(AppAuthAvailability.NotDeployed, false, false, false, null, 404, "NOT_DEPLOYED", null)) == (DoctorStatus.Warn, "acct.doc.api.notDeployed"), "");
        check("doctor", "api: 5xx → Fail 'มีปัญหาชั่วคราว'", ConnectionDoctor.ClassifyApi(new AppAuthHealth(AppAuthAvailability.Unreachable, false, false, false, null, 503, "SERVER_ERROR", null)).ReasonKey == "acct.doc.api.serverError", "");
        check("doctor", "api: 429 → Warn", ConnectionDoctor.ClassifyApi(new AppAuthHealth(AppAuthAvailability.Unreachable, false, false, false, null, 429, "RATE_LIMIT_EXCEEDED", null)).Status == DoctorStatus.Warn, "");
        check("doctor", "api: HTML → captive Wi-Fi / Cloudflare", ConnectionDoctor.ClassifyApi(new AppAuthHealth(AppAuthAvailability.Unreachable, false, false, false, null, 200, "NOT_JSON", null)).ReasonKey == "acct.doc.api.notJson", "");
        check("doctor", "tls: fine → no reason", ConnectionDoctor.ClassifyTls(SslPolicyErrors.None, "CN=Google Trust Services").ReasonKey == null, "");
        var kav = ConnectionDoctor.ClassifyTls(SslPolicyErrors.RemoteCertificateChainErrors, "CN=Kaspersky Anti-Virus Personal Root Certificate");
        check("doctor", "tls: chain error from Kaspersky → names the interceptor", kav.ReasonKey == "acct.doc.tls.fail" && kav.Interceptor == "Kaspersky", $"{kav}");
        check("doctor", "tls: chain error, unknown issuer → generic reason", ConnectionDoctor.ClassifyTls(SslPolicyErrors.RemoteCertificateChainErrors, "CN=Corp Root") is ("acct.doc.tls.fail", null), "");
        check("doctor", "clock skew in minutes", Math.Abs(ConnectionDoctor.ClockSkewMinutes(DateTime.UtcNow.AddMinutes(-12), DateTime.UtcNow) + 12) < 0.01, "");
        check("doctor", "report id CX-yyyyMMdd-XXXX", System.Text.RegularExpressions.Regex.IsMatch(ConnectionDoctor.NewReportId(new DateTime(2026, 11, 8), new Random(1)), "^CX-20261108-[0-9A-F]{4}$"), "");
        check("doctor", "key masked ABCD…WXYZ", ConnectionDoctor.MaskKey("ABCD-EFGH-IJKL-WXYZ") == "ABCD…WXYZ", ConnectionDoctor.MaskKey("ABCD-EFGH-IJKL-WXYZ"));
        string machine = XmanLicenseService.GetMachineFingerprint();
        string dirty = $"key DEMO-AB12-CD34-WX99 password=hunter2 device_code: {new string('a', 64)} id {machine}";
        string clean = ConnectionDoctor.Sanitize(dirty);
        check("doctor", "report masks keys, passwords, device codes and the machine id",
            !clean.Contains("AB12-CD34") && !clean.Contains("hunter2") && !clean.Contains(new string('a', 64)) && !clean.Contains(machine) && clean.Contains("DEMO…WX99"), clean);

        var doctor = new ConnectionDoctor(settings, account, loc) { UpdatesUrl = $"http://localhost:{port}/github/releases/latest" };
        Mode = "";   // the server today
        var rep = await doctor.RunAsync();
        string Steps() => string.Join(" ", rep.Steps.Select(s => $"{s.Id}={s.Status}"));
        check("doctor", "run against the server of today: network/dns/tcp OK, tls skipped (http), api Warn not-deployed",
            rep.Steps.First(s => s.Id == "network").Status == DoctorStatus.Ok && rep.Steps.First(s => s.Id == "dns").Status == DoctorStatus.Ok
            && rep.Steps.First(s => s.Id == "tcp").Status == DoctorStatus.Ok && rep.Steps.First(s => s.Id == "tls").Status == DoctorStatus.Skip
            && rep.Steps.First(s => s.Id == "api").Status == DoctorStatus.Warn, Steps());
        check("doctor", "clock checked from the 404's Date header", rep.Steps.First(s => s.Id == "clock").Status == DoctorStatus.Ok, Steps());
        check("doctor", "updates step reads the latest tag", rep.Steps.First(s => s.Id == "updates") is { Status: DoctorStatus.Ok } u && u.Detail.Contains("v3.0.999"), rep.Steps.First(s => s.Id == "updates").Detail);
        check("doctor", "cause = the not-deployed sentence (Thai)", rep.Cause == "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป", rep.Cause ?? "");
        check("doctor", "report: header, device = 8 chars only, a line per step, Last error, สาเหตุ",
            rep.Text.StartsWith("CluadeX connection report") && rep.Text.Contains($"Device: {machine[..8]}") && !rep.Text.Contains(machine)
            && rep.Text.Contains("[WARN] api") && rep.Text.Contains("Last error: ") && rep.Text.Contains("สาเหตุ: "), rep.Text);
        check("doctor", "report saved under <data>\\logs", rep.SavedPath != null && File.Exists(rep.SavedPath)
            && Path.GetDirectoryName(rep.SavedPath)!.Equals(Path.Combine(settings.DataRoot, "logs"), StringComparison.OrdinalIgnoreCase), rep.SavedPath ?? "");
        Fault = "captive";
        rep = await doctor.RunAsync();
        check("doctor", "captive portal → api Fail with the Wi-Fi sentence", rep.Steps.First(s => s.Id == "api").Status == DoctorStatus.Fail
            && rep.Cause == loc.T("acct.doc.api.notJson"), Steps());
        Fault = "";
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", "http://127.0.0.1:1/api/v1/product/cluadex-ai-coding-assistant");
        var deadDoctor = new ConnectionDoctor(settings, new XmanAccountService(settings, new XmanLicenseService(settings), activation2), loc)
        { UpdatesUrl = $"http://localhost:{port}/github/releases/latest" };
        rep = await deadDoctor.RunAsync();
        check("doctor", "port refused → tcp Fail (Thai reason), api/clock/updates skipped",
            rep.Steps.First(s => s.Id == "tcp").Status == DoctorStatus.Fail && rep.Steps.Where(s => s.Id is "api" or "clock" or "updates").All(s => s.Status == DoctorStatus.Skip)
            && rep.Cause == loc.T("acct.doc.tcp.fail", 1), Steps() + " · " + rep.Cause);
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", null);
        listener.Stop();
    }

    // ─── the mock ───────────────────────────────────────────────────────────────────────

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            try { Handle(ctx); } catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        string body;
        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
        lock (Calls) Calls.Add((path, body));
        JsonElement req = default;
        try { if (body.Length > 0) req = JsonDocument.Parse(body).RootElement.Clone(); } catch { }

        if (path.EndsWith("/github/releases/latest")) { Json(ctx, 200, "{\"tag_name\":\"v3.0.999\"}"); return; }

        switch (Fault)
        {
            case "503": Html(ctx, 503, "<html>Service Unavailable</html>"); return;
            case "429": Html(ctx, 429, "<html>Too Many Requests</html>"); return;
            case "captive": Html(ctx, 200, "<html><body>Please log in to the hotel Wi-Fi</body></html>"); return;
            case "laravel404": Json(ctx, 404, "{\"message\":\"The route api/v1/product/x/app-auth/health could not be found.\"}"); return;
        }

        if (path.EndsWith("/activate"))
        {
            bool force = Str(req, "force_rebind") == "true";
            if (BoundMachine != Str(req, "machine_id") && !force) { Json(ctx, 403, "{\"success\":false,\"error_code\":\"ALREADY_ACTIVATED_OTHER_DEVICE\",\"message\":\"ใช้อยู่บนเครื่องอื่น\"}"); return; }
            BoundMachine = Str(req, "machine_id");
            Json(ctx, 200, "{\"success\":true,\"data\":{\"license_key\":\"BUY1-AAAA-BBBB-CCCC\",\"license_type\":\"yearly\",\"expires_at\":\"" + DateTime.UtcNow.AddDays(300).ToString("o") + "\"}}");
            return;
        }

        if (!path.Contains("/app-auth/") || Mode != "up") { Html(ctx, 404, "<html><title>404 Not Found</title></html>"); return; }

        if (path.EndsWith("/app-auth/health"))
        {
            Json(ctx, 200, "{\"success\":true,\"data\":{\"server_time\":\"" + DateTime.UtcNow.ToString("o") + "\",\"product\":{\"slug\":\"cluadex-ai-coding-assistant\",\"name\":\"CluadeX\"},"
                + "\"capabilities\":{\"password_login\":true,\"device_login\":true,\"free_license\":true}}}");
            return;
        }
        if (path.EndsWith("/app-auth/login"))
        {
            switch (Str(req, "login"))
            {
                case "wrong@x.com": Json(ctx, 401, "{\"success\":false,\"error_code\":\"INVALID_CREDENTIALS\",\"message\":\"x\"}"); return;
                case "disabled@x.com": Json(ctx, 403, "{\"success\":false,\"error_code\":\"ACCOUNT_DISABLED\"}"); return;
                case "flood@x.com": Json(ctx, 429, "{\"success\":false,\"error_code\":\"RATE_LIMIT_EXCEEDED\"}"); return;
                case "weird@x.com": Json(ctx, 400, "{\"success\":false,\"error_code\":\"SOMETHING_NEW\"}"); return;
                case "nolicense@x.com": Json(ctx, 200, Link("no***@x.com", null)); return;
                default:
                    Json(ctx, 200, Link("bu***@x.com", "{\"license_key\":\"BUY1-AAAA-BBBB-CCCC\",\"license_type\":\"yearly\",\"expires_at\":\"" + DateTime.UtcNow.AddDays(300).ToString("o")
                        + "\",\"source\":\"purchased\",\"bound_to_other_device\":true}"));
                    return;
            }
        }
        if (path.EndsWith("/app-auth/device/start"))
        {
            string secret = "devsecret" + new string('f', 55);
            lock (Devices) Devices[secret] = "pending";
            Json(ctx, 200, "{\"success\":true,\"data\":{\"device_code\":\"" + secret + "\",\"user_code\":\"KQ7M-4TZP\",\"verification_uri\":\"https://xman4289.com/device\","
                + "\"verification_uri_complete\":\"https://xman4289.com/device?code=KQ7M-4TZP\",\"expires_in\":600,\"interval\":3}}");
            return;
        }
        if (path.EndsWith("/app-auth/device/poll"))
        {
            string state;
            lock (Devices) { state = Devices.TryGetValue(Str(req, "device_code") ?? "", out var s) ? s : "unknown"; }
            switch (state)
            {
                case "pending": Json(ctx, 200, "{\"success\":false,\"error_code\":\"AUTHORIZATION_PENDING\"}"); return;
                case "slow": Json(ctx, 200, "{\"success\":false,\"error_code\":\"SLOW_DOWN\"}"); return;
                case "denied": Json(ctx, 200, "{\"success\":false,\"error_code\":\"ACCESS_DENIED\"}"); return;
                case "approved":
                    lock (Devices) Devices[Str(req, "device_code")!] = "consumed";
                    Json(ctx, 200, Link("qr***@x.com", null));
                    return;
                default: Json(ctx, 200, "{\"success\":false,\"error_code\":\"EXPIRED_TOKEN\"}"); return;
            }
        }
        if (path.EndsWith("/app-auth/free-license"))
        {
            if (!FreeAllowed) { Json(ctx, 403, "{\"success\":false,\"error_code\":\"FREE_LICENSE_NOT_AVAILABLE\"}"); return; }
            Json(ctx, 200, "{\"success\":true,\"data\":{\"license\":{\"license_key\":\"FREE-1111-2222-3333\",\"license_type\":\"free\",\"expires_at\":null,\"source\":\"free\"},\"account_linked\":false}}");
            return;
        }
        Html(ctx, 404, "<html>404</html>");
    }

    static string Link(string email, string license) =>
        "{\"success\":true,\"data\":{\"account\":{\"name\":\"Tester\",\"email_masked\":\"" + email + "\"},\"account_linked\":true,"
        + "\"license\":" + (license ?? "null") + ",\"purchase_url\":\"https://xman4289.com/products/cluadex-ai-coding-assistant\"}}";

    static void Json(HttpListenerContext ctx, int status, string json) => Write(ctx, status, "application/json", json);
    static void Html(HttpListenerContext ctx, int status, string html) => Write(ctx, status, "text/html", html);

    static void Write(HttpListenerContext ctx, int status, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type + "; charset=utf-8";
        ctx.Response.Headers["Date"] = DateTime.UtcNow.ToString("r");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    static JsonElement LastBody(string path)
    {
        lock (Calls)
        {
            var c = Calls.LastOrDefault(x => x.Path.EndsWith(path));
            return c.Body is { Length: > 0 } b ? JsonDocument.Parse(b).RootElement.Clone() : default;
        }
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
