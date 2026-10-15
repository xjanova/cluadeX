using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CluadeX.Services;

namespace ApiTest;

/// <summary>
/// The license flow without touching xman4289.com: the real ActivationService + XmanLicenseService talk to a
/// localhost copy of the backend's ProductLicenseController rules (register-device, check-machine, demo,
/// activate, validate, deactivate, pricing — request fields and response shapes as in the PHP source). Proves
/// a fresh install registers and gets its trial key once, never asks again (asking after a trial is what the
/// server flags as abuse), a reinstall takes back its license, an expired trial is dropped, and a rate limit or
/// a 5xx never wipes a key. The debug-build override CLUADEX_LICENSE_API points the client here.
/// </summary>
internal static class MockLicense
{
    sealed class Device { public string Status = "pending"; public int TrialAttempts; public DateTime? TrialExpires; }
    sealed class License { public string Type = ""; public DateTime? Expires; public string MachineId; public string Status = "active"; }

    static readonly Dictionary<string, Device> Devices = new();
    static readonly Dictionary<string, License> Licenses = new();
    static readonly List<(string Path, string Body)> Calls = new();
    /// <summary>"" = behave; "429" / "500" / "html" = answer every call that way.</summary>
    static string Fault = "";
    static string PurchaseUrl = "https://xman4289.com/products/cluadex-ai-coding-assistant";

    static int CallCount(string path) { lock (Calls) return Calls.Count(c => c.Path.EndsWith(path)); }

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · license: register, free trial, restore, expiry (localhost mock of xman4289.com)");

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", $"http://localhost:{port}/api/v1/product/cluadex-ai-coding-assistant");

        string me = XmanLicenseService.GetMachineFingerprint();
        var svc = new SettingsService();   // portable — never the user's settings
        void Wipe() => svc.UpdateSettings(s =>
        {
            s.ActivationKey = null; s.LicenseType = null; s.LicenseExpiresUtc = null; s.LicenseValidatedUtc = null;
            s.LicenseTrialRequested = false; s.LicenseTrialEndedNoticeShown = false;
        });
        Wipe();

        // ── 1. Fresh install on a PC the server has never seen ──
        var notices = new List<LicenseNotice>();
        var a1 = new ActivationService(svc, new XmanLicenseService(svc));
        a1.Notice += n => notices.Add(n);
        check("license", "fresh install starts on Free", !a1.IsActivated && a1.ActivationTier == "free", a1.TierDisplayName);
        await a1.ValidateOnlineAsync();
        await a1.AutoProvisionAsync();

        var reg = LastBody("/register-device");
        check("license", "registers the PC: 64-hex machine_id + machine_name + app_version",
            Str(reg, "machine_id") == me && me.Length == 64 && Str(reg, "machine_name") == Environment.MachineName
            && !string.IsNullOrEmpty(Str(reg, "app_version")), reg.GetRawText());
        check("license", "asks check-machine before any trial", CallCount("/check-machine") == 1, "");
        check("license", "fresh PC gets its free trial key right away", a1.IsTrial && (svc.Settings.ActivationKey ?? "").StartsWith("DEMO-"),
            $"tier={a1.ActivationTier} key={(svc.Settings.ActivationKey ?? "(none)")[..Math.Min(5, (svc.Settings.ActivationKey ?? "(none)").Length)]}…");
        check("license", "trial-started notice raised once", notices.Count(n => n == LicenseNotice.TrialStarted) == 1, string.Join(",", notices));
        var exp = a1.ExpiresAtUtc;
        check("license", "expiry read as UTC (no local-offset shift)",
            exp is { Kind: DateTimeKind.Utc } e1 && Math.Abs((e1 - DateTime.UtcNow.AddDays(1)).TotalMinutes) < 2, $"{exp:O}");
        check("license", "install remembers it asked", svc.Settings.LicenseTrialRequested, "");
        check("license", "trial unlocks the Pro features", a1.IsFeatureUnlocked("feature.github"), "");

        // ── 2. Next launch: cached state first, then the server ──
        var a2 = new ActivationService(svc, new XmanLicenseService(svc));
        check("license", "next launch shows the trial before any network", a2.IsTrial, a2.TierDisplayName);
        await a2.ValidateOnlineAsync();
        await a2.AutoProvisionAsync();
        check("license", "a PC with a key never asks for another trial", CallCount("/demo") == 1, $"/demo calls={CallCount("/demo")}");

        // ── 3. The trial runs out (server side), app restarts ──
        foreach (var l in Licenses.Values.Where(l => l.Type == "demo")) l.Expires = DateTime.UtcNow.AddMinutes(-5);
        foreach (var d in Devices.Values) d.TrialExpires = DateTime.UtcNow.AddMinutes(-5);
        svc.UpdateSettings(s => s.LicenseExpiresUtc = DateTime.UtcNow.AddMinutes(-5));
        notices.Clear();
        var a3 = new ActivationService(svc, new XmanLicenseService(svc));
        a3.Notice += n => notices.Add(n);
        check("license", "expired trial is off before any network", !a3.IsActivated, a3.TierDisplayName);
        await a3.ValidateOnlineAsync();
        check("license", "server says expired → key dropped", svc.Settings.ActivationKey == null, "");
        await a3.AutoProvisionAsync();
        check("license", "after the trial: no second /demo (that gets a PC flagged)", CallCount("/demo") == 1, $"/demo calls={CallCount("/demo")}");
        check("license", "trial-ended invitation raised exactly once", notices.Count(n => n == LicenseNotice.TrialEnded) == 1, string.Join(",", notices));
        check("license", "no trial offered in the dialog afterwards", !a3.CanRequestTrial, "");
        var a3b = new ActivationService(svc, new XmanLicenseService(svc));
        var again = new List<LicenseNotice>(); a3b.Notice += n => again.Add(n);
        await a3b.ValidateOnlineAsync(); await a3b.AutoProvisionAsync();
        check("license", "…and not repeated on the next launch", again.Count == 0, string.Join(",", again));

        // ── 4. Reinstall (settings gone) on a PC holding a bought license ──
        Licenses["CLX-AAAA-BBBB-CCCC"] = new License { Type = "lifetime", MachineId = me };
        Devices[me].Status = "licensed";
        Wipe();
        notices.Clear();
        var a4 = new ActivationService(svc, new XmanLicenseService(svc));
        a4.Notice += n => notices.Add(n);
        await a4.ValidateOnlineAsync();
        await a4.AutoProvisionAsync();
        check("license", "reinstall takes back the PC's license, nothing typed",
            a4.IsLifetime && svc.Settings.ActivationKey == "CLX-AAAA-BBBB-CCCC", a4.TierDisplayName);
        check("license", "restore notice raised", notices.Contains(LicenseNotice.Restored), string.Join(",", notices));
        check("license", "…and still no /demo", CallCount("/demo") == 1, "");

        // ── 5. A hiccup is not a verdict ──
        foreach (var fault in new[] { "429", "500", "html" })
        {
            Fault = fault;
            var af = new ActivationService(svc, new XmanLicenseService(svc));
            await af.ValidateOnlineAsync();
            Fault = "";
            check("license", $"validate answered {fault} → key kept", svc.Settings.ActivationKey == "CLX-AAAA-BBBB-CCCC" && af.IsActivated, "");
        }

        // ── 6. Offline grace ──
        svc.UpdateSettings(s => s.LicenseValidatedUtc = DateTime.UtcNow.AddDays(-31));
        check("license", "unconfirmed for 31 days → off until the server confirms", !new ActivationService(svc, new XmanLicenseService(svc)).IsActivated, "");
        svc.UpdateSettings(s => s.LicenseValidatedUtc = DateTime.UtcNow.AddDays(10));
        check("license", "confirmation dated in the future → off", !new ActivationService(svc, new XmanLicenseService(svc)).IsActivated, "");
        svc.UpdateSettings(s => s.LicenseValidatedUtc = DateTime.UtcNow.AddDays(-5));
        check("license", "confirmed 5 days ago, offline → still on", new ActivationService(svc, new XmanLicenseService(svc)).IsActivated, "");

        // ── 7. Typing a key ──
        Wipe();
        svc.UpdateSettings(s => s.LicenseTrialRequested = true);
        var a7 = new ActivationService(svc, new XmanLicenseService(svc));
        var (ok1, _, code1) = await a7.ActivateAsync("NOPE-1111-2222-3333");
        check("license", "unknown key → INVALID_LICENSE", !ok1 && code1 == "INVALID_LICENSE", code1 ?? "(none)");
        Licenses["CLX-OTHR-PC00-0001"] = new License { Type = "yearly", Expires = DateTime.UtcNow.AddDays(300), MachineId = new string('b', 64) };
        string conflictKey = null;
        a7.DeviceConflictDetected += (k, _) => conflictKey = k;
        var (ok2, _, code2) = await a7.ActivateAsync("clx-othr-pc00-0001");
        check("license", "key active on another PC → asks to move it", !ok2 && code2 == "ALREADY_ACTIVATED_OTHER_DEVICE" && conflictKey != null, code2 ?? "");
        var (ok3, _) = await a7.ActivateForceAsync(conflictKey ?? "");
        check("license", "move confirmed → active here, yearly = Pro with an end date",
            ok3 && a7.ActivationTier == "pro" && a7.ExpiresAtUtc != null && Licenses["CLX-OTHR-PC00-0001"].MachineId == me, a7.TierDisplayName);
        await a7.DeactivateAsync();
        check("license", "deactivate clears the key and frees the license", !a7.IsActivated && svc.Settings.ActivationKey == null
            && Licenses["CLX-OTHR-PC00-0001"].MachineId == null, "");

        // ── 8. A trial running out while the app is open ──
        Wipe();
        Devices.Clear();
        Licenses.Clear();
        int demoBefore = CallCount("/demo");
        var a8 = new ActivationService(svc, new XmanLicenseService(svc));
        var n8 = new List<LicenseNotice>(); a8.Notice += n => n8.Add(n);
        int changed = 0; a8.ActivationChanged += () => changed++;
        await a8.ValidateOnlineAsync(); await a8.AutoProvisionAsync();
        check("license", "another fresh PC → trial", a8.IsTrial && CallCount("/demo") == demoBefore + 1, a8.TierDisplayName);
        // Rewind the cached expiry through a new instance (the running one reads it from its own state).
        svc.UpdateSettings(s => s.LicenseExpiresUtc = DateTime.UtcNow.AddSeconds(1));
        var a8b = new ActivationService(svc, new XmanLicenseService(svc));
        var n8b = new List<LicenseNotice>(); a8b.Notice += n => n8b.Add(n);
        int changedB = 0; a8b.ActivationChanged += () => changedB++;
        check("license", "trial on with a second to go", a8b.IsTrial, "");
        await Task.Delay(1500);
        a8b.CheckExpiry();
        check("license", "the minute tick turns it off and invites once", !a8b.IsActivated && changedB == 1
            && n8b.Count(n => n == LicenseNotice.TrialEnded) == 1, $"changed={changedB} notices={string.Join(",", n8b)}");
        a8b.CheckExpiry();
        check("license", "a second tick says nothing more", changedB == 1 && n8b.Count == 1, "");

        // ── 9. The dialog's trial button pressed while startup is already asking ──
        Wipe();
        Devices.Clear();
        Licenses.Clear();
        int demoBefore9 = CallCount("/demo");
        var a9 = new ActivationService(svc, new XmanLicenseService(svc));
        var both = await Task.WhenAll(a9.StartTrialAsync(), a9.StartTrialAsync());
        check("license", "two trial requests at once → one /demo, one key", CallCount("/demo") == demoBefore9 + 1
            && both.Count(r => r.success) == 1 && a9.IsTrial, $"/demo +{CallCount("/demo") - demoBefore9}, ok={both.Count(r => r.success)}");

        // ── 10. Pricing + purchase link ──
        var lic = new XmanLicenseService(svc);
        var pricing = await lic.GetPricingAsync();
        check("license", "pricing: the store's plans", pricing.Plans.Any(p => p.Type == "yearly" && p.Price == 199)
            && pricing.Plans.Any(p => p.Type == "lifetime" && p.Price == 1999 && p.DurationDays == null),
            string.Join(", ", pricing.Plans.Select(p => $"{p.Type}={p.Price}")));
        check("license", "purchase link from the API kept when on xman4289.com", pricing.PurchaseUrl == PurchaseUrl, pricing.PurchaseUrl);
        PurchaseUrl = "https://github.com/someone/repo";
        var leak = await lic.GetPricingAsync();
        check("license", "purchase link elsewhere → replaced by the xman4289.com store", leak.PurchaseUrl == XmanLicenseService.DefaultPurchaseUrl, leak.PurchaseUrl);
        check("license", "plain-http or look-alike hosts refused",
            XmanLicenseService.SafeStoreUrl("http://xman4289.com/x") == XmanLicenseService.DefaultPurchaseUrl
            && XmanLicenseService.SafeStoreUrl("https://xman4289.com.evil.io/x") == XmanLicenseService.DefaultPurchaseUrl, "");

        Wipe();
        Environment.SetEnvironmentVariable("CLUADEX_LICENSE_API", null);
        listener.Stop();
    }

    // ─── the mock backend ───

    // Invariant: on a Thai-culture PC the default calendar is Buddhist and "yyyy" would print 2569.
    static string Iso(DateTime? utc) => utc is { } u
        ? u.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) : null;

    static bool CanStartTrial(Device d)
        => !(d.Status == "trial" && d.TrialExpires > DateTime.UtcNow) && d.Status != "licensed" && d.Status != "blocked"
           && d.TrialAttempts < 3;

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            string path = ctx.Request.Url!.AbsolutePath;
            lock (Calls) Calls.Add((path, body));

            (int status, string json) = Fault switch
            {
                "429" => (429, "{\"message\":\"Too Many Attempts.\"}"),
                "500" => (500, "{\"message\":\"Server Error\"}"),
                "html" => (200, "<!doctype html><html><body>maintenance</body></html>"),
                _ => Handle(path, body),
            };
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = Fault == "html" ? "text/html" : "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    static (int, string) Handle(string path, string body)
    {
        JsonElement b = body.Length > 0 ? JsonDocument.Parse(body).RootElement.Clone() : default;
        string mid = b.ValueKind == JsonValueKind.Object ? Str(b, "machine_id") : null;
        string action = path[(path.LastIndexOf('/') + 1)..];

        if (action == "pricing")
            return (200, JsonSerializer.Serialize(new
            {
                success = true,
                data = new
                {
                    product = new { name = "CluadeX", slug = "cluadex-ai-coding-assistant" },
                    plans = new Dictionary<string, object>
                    {
                        ["yearly"] = new { price = 199, currency = "THB", duration_days = (int?)365 },
                        ["lifetime"] = new { price = 1999, currency = "THB", duration_days = (int?)null },
                    },
                    purchase_url = PurchaseUrl,
                },
            }));

        if (mid is not { Length: >= 32 and <= 64 })
            return (422, "{\"message\":\"The machine id field is required.\",\"errors\":{\"machine_id\":[\"required\"]}}");

        lock (Devices)
        {
            if (!Devices.TryGetValue(mid, out var dev)) { dev = new Device(); if (action is "register-device" or "demo") Devices[mid] = dev; }
            string key = Str(b, "license_key")?.Trim().ToUpperInvariant();
            Licenses.TryGetValue(key ?? "", out var lic);

            switch (action)
            {
                case "register-device":
                    return (200, JsonSerializer.Serialize(new
                    {
                        success = true,
                        data = new
                        {
                            device_status = dev.Status,
                            is_suspicious = false,
                            can_start_trial = CanStartTrial(dev),
                            trial_info = dev.Status == "trial"
                                ? new { expires_at = Iso(dev.TrialExpires), days_remaining = 0, is_expired = !(dev.TrialExpires > DateTime.UtcNow) }
                                : null,
                        },
                    }));

                case "check-machine":
                    var held = Licenses.FirstOrDefault(kv => kv.Value.MachineId == mid && kv.Value.Status == "active"
                        && (kv.Value.Expires == null || kv.Value.Expires > DateTime.UtcNow));
                    return held.Key == null
                        ? (200, "{\"success\":true,\"has_license\":false,\"message\":\"ไม่พบ License สำหรับเครื่องนี้\"}")
                        : (200, JsonSerializer.Serialize(new
                        {
                            success = true, has_license = true,
                            data = new { license_key = held.Key, license_type = held.Value.Type, status = "active", expires_at = Iso(held.Value.Expires) },
                        }));

                case "demo":
                    if (!CanStartTrial(dev))
                        return (403, JsonSerializer.Serialize(new { success = false, error_code = dev.Status == "trial" && dev.TrialExpires > DateTime.UtcNow ? "TRIAL_ACTIVE" : "TRIAL_NOT_AVAILABLE", message = "ไม่สามารถเริ่ม Trial ได้" }));
                    dev.Status = "trial"; dev.TrialAttempts++; dev.TrialExpires = DateTime.UtcNow.AddDays(1);
                    string demoKey = "DEMO-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant() + "-ABCD-EFGH";
                    Licenses[demoKey] = new License { Type = "demo", Expires = dev.TrialExpires, MachineId = mid };
                    return (200, JsonSerializer.Serialize(new
                    {
                        success = true, message = "เริ่มใช้งาน Trial 1 วันสำเร็จ",
                        data = new { license_key = demoKey, expires_at = Iso(dev.TrialExpires), days_remaining = 1, features = new[] { "basic_features", "trial_mode" } },
                    }));

                case "validate":
                    if (lic == null || lic.MachineId != mid)
                        return (404, "{\"success\":false,\"is_valid\":false,\"error_code\":\"INVALID_LICENSE\",\"message\":\"License ไม่ถูกต้องหรือไม่ตรงกับเครื่อง\"}");
                    bool valid = lic.Status == "active" && (lic.Type == "lifetime" || lic.Expires > DateTime.UtcNow);
                    return (200, JsonSerializer.Serialize(new
                    {
                        success = true, is_valid = valid,
                        data = new { license_key = key, license_type = lic.Type, status = lic.Status, expires_at = Iso(lic.Expires), is_expired = !valid },
                    }));

                case "activate":
                    if (lic == null) return (404, "{\"success\":false,\"error_code\":\"INVALID_LICENSE\",\"message\":\"License key ไม่ถูกต้อง\"}");
                    if (lic.Type != "lifetime" && !(lic.Expires > DateTime.UtcNow))
                        return (403, "{\"success\":false,\"error_code\":\"LICENSE_EXPIRED\",\"message\":\"License หมดอายุแล้ว\"}");
                    if (lic.MachineId != null && lic.MachineId != mid && Str(b, "force_rebind") != "true")
                        return (403, "{\"success\":false,\"error_code\":\"ALREADY_ACTIVATED_OTHER_DEVICE\",\"message\":\"License นี้ถูกใช้งานบนเครื่องอื่นแล้ว\"}");
                    if (string.IsNullOrEmpty(Str(b, "machine_fingerprint")))
                        return (422, "{\"message\":\"The machine fingerprint field is required.\"}");
                    lic.MachineId = mid;
                    Devices[mid] = dev; dev.Status = "licensed";
                    return (200, JsonSerializer.Serialize(new
                    {
                        success = true, message = "เปิดใช้งาน License สำเร็จ",
                        data = new { license_key = key, license_type = lic.Type, expires_at = Iso(lic.Expires) },
                    }));

                case "deactivate":
                    if (lic == null || lic.MachineId != mid)
                        return (404, "{\"success\":false,\"error_code\":\"INVALID_LICENSE\",\"message\":\"License ไม่ถูกต้องหรือไม่ตรงกับเครื่อง\"}");
                    lic.MachineId = null; dev.Status = "pending";
                    return (200, "{\"success\":true,\"message\":\"ยกเลิกการเปิดใช้งาน License สำเร็จ\"}");
            }
        }
        return (404, "{\"message\":\"no route\"}");
    }

    static JsonElement LastBody(string pathPart)
    {
        lock (Calls)
        {
            var hit = Calls.LastOrDefault(c => c.Path.EndsWith(pathPart) && c.Body.Length > 0);
            return JsonDocument.Parse(hit.Body ?? "{}").RootElement.Clone();
        }
    }

    static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
