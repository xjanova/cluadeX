// The colibri plugin (owner 2026-11-08: "ทำเรื่อง colibri ต่อเป็น plugin เสริม … งานไหนใช้การ์ดจอ local ได้ ยิ่งดีเลย …
// ทำประวัติกราฟชี้ชัดว่าประหยัดได้เท่าไหร่ด้วย … เอไอมาสคอตสาวตัวไหนก็ทำให้ครบ"). Checks the pure parts offline: which release
// files are trusted, checksums, version order, colibri's JSON (models and status), the archive guard, the model-id guard,
// the MoE size, the local tier rule (cheap 7B+, mid 30B+, never deep, never back to the same local model), the on-PC
// saving in the ledger (booked, then taken back on a climb), and every provider's girl. Nothing is downloaded or run.
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Colibri;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class ColibriWire
{
    const string Release = """
    { "tag_name": "v2.1.0", "assets": [
      { "name": "colibri-v2.1.0-windows-x86_64.zip", "browser_download_url": "https://github.com/JustVugg/colibri/releases/download/v2.1.0/colibri-v2.1.0-windows-x86_64.zip" },
      { "name": "colibri-v2.1.0-windows-x86_64-cuda.zip", "browser_download_url": "https://github.com/JustVugg/colibri/releases/download/v2.1.0/colibri-v2.1.0-windows-x86_64-cuda.zip" },
      { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/JustVugg/colibri/releases/download/v2.1.0/SHA256SUMS.txt" },
      { "name": "colibri-v2.1.0-linux-x86_64.tar.gz", "browser_download_url": "https://github.com/JustVugg/colibri/releases/download/v2.1.0/colibri-v2.1.0-linux-x86_64.tar.gz" }
    ] }
    """;

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · colibri plugin (installer facts, local tiers, on-PC savings, mascots)");
        Facts(check);
        Tiers(check);
        Ledger(check);
        Mascots(check);
        return Task.CompletedTask;
    }

    static void Facts(Action<string, string, bool, string> check)
    {
        var r = ColibriFacts.PickRelease(Release);
        check("colibri-release", "the Windows zip, the CUDA zip and the checksum list are picked from the release",
            r is { Tag: "v2.1.0", ZipName: "colibri-v2.1.0-windows-x86_64.zip", CudaZipName: "colibri-v2.1.0-windows-x86_64-cuda.zip" }
            && r.SumsUrl.EndsWith("/SHA256SUMS.txt"), r?.ToString() ?? "null");

        string evil = Release.Replace("https://github.com/JustVugg/colibri/releases/download/v2.1.0/colibri-v2.1.0-windows-x86_64.zip",
            "https://evil.example/colibri-v2.1.0-windows-x86_64.zip");
        check("colibri-release", "a Windows file hosted anywhere but the repo's own release download is ignored (no install)",
            ColibriFacts.PickRelease(evil) == null, "");
        string noSums = Release.Replace("SHA256SUMS.txt", "NOTES.txt");
        check("colibri-release", "a release with no checksum list is not installed", ColibriFacts.PickRelease(noSums) == null, "");

        string hex = new string('a', 64), hex2 = new string('B', 64);
        string sums = $"{hex}  colibri-v2.1.0-windows-x86_64.zip\n{hex2} *colibri-v2.1.0-windows-x86_64-cuda.zip\nnot a line\n";
        check("colibri-sha", "checksums are read in both sha256sum styles, lower-cased; a file not listed has none",
            ColibriFacts.ChecksumFor(sums, "colibri-v2.1.0-windows-x86_64.zip") == hex
            && ColibriFacts.ChecksumFor(sums, "colibri-v2.1.0-windows-x86_64-cuda.zip") == hex2.ToLowerInvariant()
            && ColibriFacts.ChecksumFor(sums, "other.zip") == null
            && ColibriFacts.ChecksumFor(sums, "colibri-v2.1.0-windows-x86_64") == null, "");

        check("colibri-update", "an update is a higher version only (v2.10 > v2.9; same or older is not; nothing installed = any)",
            ColibriFacts.IsNewer("v2.10.0", "v2.9.3") && !ColibriFacts.IsNewer("v2.1.0", "v2.1.0") && !ColibriFacts.IsNewer("v2.0.9", "v2.1.0")
            && ColibriFacts.IsNewer("v2.1.1-rc1", "v2.1.0") && ColibriFacts.IsNewer("v2.1.0", null) && !ColibriFacts.IsNewer("latest", "v2.1.0"), "");

        check("colibri-cuda", "the CUDA build only for compute 8.0+ (a GTX 1070 Ti is 6.1 → Vulkan; none → no CUDA)",
            !ColibriFacts.WantsCuda(6.1) && ColibriFacts.WantsCuda(8.6) && ColibriFacts.WantsCuda(8.0) && !ColibriFacts.WantsCuda(null), "");

        const string models = """
        Using 2 models
        [ {"id":"flux-schnell","name":"FLUX schnell","kind":"image","download_gb":12,"fits":true,"recommended":true},
          {"id":"qwen36-35b-a3b","name":"Qwen3.6 35B-A3B","kind":"text","download_gb":21.5,"ram_min_gb":14,"ram_good_gb":24,
           "runs":"from RAM","status":"ok","fits":true,"recommended":true,"summary":"best all-round","license_note":"Apache-2.0"},
          {"id":"deepseek-v4","name":"DeepSeek V4","kind":"text","download_gb":400,"fits":false,"reason":"needs 420 GB free"} ]
        """;
        var list = ColibriFacts.ParseModels(models);
        var pick = ColibriFacts.DefaultPick(list);
        check("colibri-models", "`setup --list --json` parses past a log line; the pick is the recommended CHAT model that fits",
            list.Count == 3 && pick?.Id == "qwen36-35b-a3b" && pick.FromRam && pick.LicenseNote == "Apache-2.0"
            && !list[0].IsChat && !list[2].Fits && list[2].Reason.Contains("420"), pick?.Id ?? "none");
        check("colibri-models", "nothing that fits → no pick (the card says so)", ColibriFacts.DefaultPick(list.Where(m => !m.Fits).ToList()) == null, "");

        const string status = """
        {"configured":true,"setup":{"model":{"id":"qwen36-35b-a3b","name":"Qwen3.6"},"backend":"vulkan"},
         "server":{"state":"ready","urls":{"browser":"http://127.0.0.1:8000/","openai_base_url":"http://127.0.0.1:8000/v1"},
                   "model_id":"qwen36-35b-a3b","tokens_per_second":11.4,"new_field":[1,2]},
         "install":{"phase":"download","running":true,"done":512,"total":2048,"rate":64,"message":"shard 3/9"}}
        """;
        var s = ColibriFacts.ParseStatus(status);
        check("colibri-status", "`status --json`: model, backend, server state, URLs, model id, speed, install progress; unknown fields ignored",
            s is { Configured: true, Model: "qwen36-35b-a3b", Backend: "vulkan", IsReady: true, ModelId: "qwen36-35b-a3b",
                   OpenAiBaseUrl: "http://127.0.0.1:8000/v1", InstallPhase: "download", InstallRunning: true }
            && s.InstallFraction == 0.25 && s.TokensPerSecond == 11.4, s.ToString());
        var empty = ColibriFacts.ParseStatus("""{"configured":false,"server":"stopped"}""");
        check("colibri-status", "a status with nothing set up (or a field of another shape) reads as not ready, never a crash",
            empty is { Configured: false, IsReady: false, ServerState: null } && ColibriFacts.ParseStatus("no json").IsReady == false, "");

        check("colibri-zip", "archive entries that climb out, are rooted or name a drive are refused",
            ColibriFacts.SafeEntry("bin/coli.exe") && ColibriFacts.SafeEntry("coli") && !ColibriFacts.SafeEntry("../x.dll")
            && !ColibriFacts.SafeEntry("a/../../x") && !ColibriFacts.SafeEntry("/etc/x") && !ColibriFacts.SafeEntry("C:\\x")
            && !ColibriFacts.SafeEntry("\\\\server\\x") && !ColibriFacts.SafeEntry(""), "");

        check("colibri-cmd", "a model id goes on a cmd line only as letters, digits and . - _",
            ColibriService.ModelIdOk("qwen36-35b-a3b") && ColibriService.ModelIdOk("gpt_oss.20b")
            && !ColibriService.ModelIdOk("x & del *") && !ColibriService.ModelIdOk("a\"b") && !ColibriService.ModelIdOk("%PATH%")
            && !ColibriService.ModelIdOk("") && !ColibriService.ModelIdOk(new string('a', 81)), "");

        string tmp = Path.Combine(Path.GetTempPath(), "cluadex-colibri-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "plugins", "colibri"));
            File.WriteAllText(Path.Combine(tmp, "plugins", "colibri", "plugin.json"), """{"InstalledTag":"v2.1.0","ModelId":"qwen36-35b-a3b"}""");
            check("colibri-state", "the catalog model id is read back from plugin.json (it names the size); none → null",
                ColibriService.CatalogModelId(tmp) == "qwen36-35b-a3b" && ColibriService.CatalogModelId(Path.Combine(tmp, "nope")) == null, "");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        var preset = CompatiblePresets.For(AiProviderType.Colibri);
        check("colibri-preset", "the Colibri provider: a local, keyless OpenAI-compatible server on localhost:8000 with its own logo",
            preset is { Local: true, KeyRequired: false, BaseUrl: "http://localhost:8000/v1", Logo: "colibri.png" }, "");
    }

    static void Tiers(Action<string, string, bool, string> check)
    {
        check("colibri-size", "a MoE counts its whole size, not the active part: qwen36-35b-a3b = 35B, Qwen3.6-35B-A3B = 35B, gpt-oss-20b = 20B",
            ModelCapability.ParseBillions("qwen36-35b-a3b") == 35 && ModelCapability.ParseBillions("Qwen3.6-35B-A3B") == 35
            && ModelCapability.ParseBillions("gpt-oss-20b") == 20,
            $"{ModelCapability.ParseBillions("qwen36-35b-a3b")}");

        var cap = ModelCapability.Tier.Capable;
        check("colibri-tier", "on this PC: cheap on a 7B+ model, mid only on a 30B+ one, deep never",
            DispatchPolicy.UseLocalFor(true, SubAgentTier.Cheap, false, false, true, true, cap, 8)
            && DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, true, true, cap, 35)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, true, true, cap, 14)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, true, true, cap, null)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Deep, false, false, true, true, cap, 400), "");
        check("colibri-tier", "never when it edits, the run is local already, the switch is off, it is down or cannot call tools",
            !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, true, false, true, true, cap, 35)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, true, true, true, cap, 35)
            && !DispatchPolicy.UseLocalFor(false, SubAgentTier.Mid, false, false, true, true, cap, 35)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, false, true, cap, 35)
            && !DispatchPolicy.UseLocalFor(true, SubAgentTier.Mid, false, false, true, false, cap, 35), "");
    }

    static void Ledger(Action<string, string, bool, string> check)
    {
        var cost = new CostTrackingService();
        // the chat on a local model: counted on the chart, no saving (nothing would have gone to the API)
        cost.RecordLocalUsage("Colibri", "qwen36-35b-a3b", 1_000, 200, 3);
        var d0 = cost.Between(DateTime.Today, DateTime.Today)[0];
        check("colibri-ledger", "local use outside a routed sub-agent is counted (requests, tokens) but books no saving",
            d0 is { LocalRequests: 1, LocalTokens: 1_200, SavedLocalUsd: 0 }, d0.ToString());

        var run = new AgentRunScope("t", null);
        var cheap = run.CreateChild();
        cheap.SubAgentTier = "cheap"; cheap.TierRunModel = "claude-opus-5-5";
        AgentRunScope.Enter(cheap);
        try { cost.RecordLocalUsage("Colibri", "qwen36-35b-a3b", 30_000, 3_000, 40); }
        finally { AgentRunScope.Enter(null); }
        var d1 = cost.Between(DateTime.Today, DateTime.Today)[0];
        check("colibri-ledger", "a sub-agent on this PC books what the run's model would have charged — in the routing AND the on-PC saving",
            d1.SavedLocalUsd > 0 && d1.SavedLocalUsd == d1.SavedRoutingUsd && d1.LocalRequests == 2 && cheap.LocalSavedUsd == d1.SavedLocalUsd,
            $"local {d1.SavedLocalUsd} routing {d1.SavedRoutingUsd}");

        cost.RecordTierClimb("cheap", cheap);
        var d2 = cost.Between(DateTime.Today, DateTime.Today)[0];
        check("colibri-ledger", "its report fell short and climbed to the API: the on-PC saving is taken back (it saved nothing)",
            d2.SavedLocalUsd == 0 && d2.SavedRoutingUsd == 0 && d2.LocalRequests == 2, $"local {d2.SavedLocalUsd} routing {d2.SavedRoutingUsd}");

        var back = JsonSerializer.Deserialize<DailyUsage>(JsonSerializer.Serialize(new DailyUsage { SavedLocalUsd = 1.5m, LocalRequests = 7, LocalTokens = 9000 }));
        check("colibri-ledger", "the on-PC columns survive the ledger's JSON; an old ledger reads them as zero",
            back is { SavedLocalUsd: 1.5m, LocalRequests: 7, LocalTokens: 9000 }
            && JsonSerializer.Deserialize<DailyUsage>("""{"Usd":1}""") is { SavedLocalUsd: 0, LocalRequests: 0 }, "");
    }

    static void Mascots(Action<string, string, bool, string> check)
    {
        var colibri = ChatCharacter.ForProvider(AiProviderType.Colibri);
        check("mascot", "น้อง Colibri: her own name and emerald, CluadeX's girl standing in until her picture is in the build",
            colibri.Own == "colibri" && colibri.Name == "Colibri" && colibri.Accent.G == 0xB9 && colibri.Image != null
            && (colibri.HasOwnArt ? colibri.Girl == "colibri" : colibri.Girl == "cluadex"), $"{colibri.Girl}");

        // every provider but a custom endpoint has a girl of its own (the CLIs share their API's girl)
        var missing = Enum.GetValues<AiProviderType>()
            .Where(t => t is not AiProviderType.OpenAICompatible && ChatCharacter.ForProvider(t) == ChatCharacter.CluadeX
                        && t is not (AiProviderType.Local or AiProviderType.LlamaServer))
            .ToList();
        check("mascot", "every provider has her own girl (only a custom endpoint and GGUF on this PC are CluadeX's)",
            missing.Count == 0, string.Join(", ", missing));
        var compat = CompatiblePresets.All.Where(p => p.Type is not AiProviderType.OpenAICompatible and not AiProviderType.XAI)
            .Select(p => ChatCharacter.ForProvider(p.Type).Own).ToList();
        check("mascot", "the compatible services' girls are all different people",
            compat.Distinct(StringComparer.OrdinalIgnoreCase).Count() == compat.Count, string.Join(",", compat));
        check("mascot", "a picker of girls lists only drawn ones; ForGirl finds a stand-in by her own name",
            ChatCharacter.All.All(c => c.HasOwnArt) && ChatCharacter.ForGirl("groq").Own == "groq"
            && ChatCharacter.ForGirl("cluadex") == ChatCharacter.CluadeX && ChatCharacter.ForGirl("nobody") == ChatCharacter.CluadeX, "");

        // the girls drawn in ChatGPT (Assets\Girls\Providers) are their own, framed on their own faces, and never join the
        // backdrop / splash pick of the main eight
        var drawn = new[] { "colibri", "openrouter", "groq", "deepseek", "mistral", "together" };
        var notOwn = drawn.Where(n => !ChatCharacter.ForGirl(n).HasOwnArt || !GirlArt.Has(n)).ToList();
        check("mascot", "the drawn provider girls show their own picture", notOwn.Count == 0, string.Join(",", notOwn));
        var badFace = drawn.Select(ChatCharacter.ForGirl)
            .Where(c => c.FaceBrush is not { } f || f.Viewbox.Left < 0 || f.Viewbox.Top < 0 || f.Viewbox.Right > 1 || f.Viewbox.Bottom > 0.25)
            .Select(c => c.Own).ToList();
        check("mascot", "each drawn provider girl's face square sits inside her picture", badFace.Count == 0, string.Join(",", badFace));
        check("mascot", "the backdrop / splash still pick from the main eight only",
            GirlArt.Names.Count == 8 && !GirlArt.Names.Intersect(drawn, StringComparer.OrdinalIgnoreCase).Any(),
            string.Join(",", GirlArt.Names));
    }
}
