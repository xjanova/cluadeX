using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CluadeX.Services.Safety;
using CluadeX.Services.Setup;
using CluadeX.Services.Uninstall;
using Microsoft.Win32;

/// <summary>
/// The uninstall page's cleaner, on a FAKE data folder made here (never ~/.cluadex): what is ticked survives, what is not
/// is gone, settings.json splits between "settings" and "license", a junction is unlinked and never followed, a locked
/// file is reported with its reason instead of stopping the run, and the owner's real Chrome key and BrainX bridges are
/// untouched by a run on another data folder.
/// </summary>
static partial class Program
{
    static void UninstallChecks()
    {
        Console.WriteLine("\n── uninstall ──");
        string work = Path.Combine(Path.GetTempPath(), "cx-uninstalltest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            Check("U1", "every kind of file lands in its row; unknown files go with settings",
                UninstallCatalog.Classify("settings.json") == "Settings" && UninstallCatalog.Classify("codex.db-wal") == "Chats"
                && UninstallCatalog.Classify("llama-backend.old-3") == "Models" && UninstallCatalog.Classify("settings.json.bak") == UninstallCatalog.SettingsBackupMarker
                && UninstallCatalog.Classify("settings.json.tmp") == UninstallCatalog.SettingsBackupMarker
                && UninstallCatalog.Classify("something-new.json") == "Settings" && UninstallCatalog.Classify("chrome") == UninstallCatalog.AlwaysMarker
                && UninstallCatalog.Classify("Logs") == "Logs" && UninstallCatalog.Classify("webview-signin") == "License" && UninstallCatalog.Classify("UiStudio") == "Studio");

            bool threw = false;
            try { UninstallCatalog.ParseKeep("chats,bogus"); } catch (ArgumentException) { threw = true; }
            Check("U2", "--keep: names, all, none; an unknown name is refused",
                threw && UninstallCatalog.ParseKeep("none").Count == 0 && UninstallCatalog.ParseKeep("all").Count == UninstallCatalog.Order.Count
                && UninstallCatalog.ParseKeep("settings, chats").SequenceEqual(new[] { KeepGroupId.Settings, KeepGroupId.Chats }));

            string empty = Path.Combine(work, "not-data");
            Directory.CreateDirectory(empty);
            bool refused = false;
            try { UninstallOptions.Parse(new[] { "--uninstall", "--data-root", empty }); } catch (ArgumentException) { refused = true; }
            Check("U3", "--data-root refuses a folder that holds no CluadeX data", refused && !UninstallCatalog.LooksLikeDataRoot(empty));

            // ── A: keep chats + license ──
            var (rootA, victimA) = MakeFakeRoot(work, "A");
            var optsA = UninstallOptions.Parse(new[] { "--uninstall", "--data-root", rootA, "--keep", "chats,license", "--yes", "--no-handoff" });
            Check("U4", "options: the fake folder, the ticks, yes, no hand-off",
                optsA.DataRootOverridden && optsA.Yes && optsA.NoHandoff && optsA.Keep!.SequenceEqual(new[] { KeepGroupId.Chats, KeepGroupId.License }));
            var scanA = DataScan.Run(rootA);
            Check("U5", "the scan sizes every row and finds the license inside settings.json",
                scanA.Bytes[KeepGroupId.Chats] > 0 && scanA.Bytes[KeepGroupId.Models] >= 3000 && scanA.HasAnything(KeepGroupId.License)
                && scanA.Always.Count == 1 && scanA.SettingsBackups.Count == 1,
                string.Join(", ", scanA.Bytes.Select(b => $"{b.Key}={b.Value}")));
            var cA = Clean(scanA, optsA.Keep!, Array.Empty<string>());
            var settingsA = JsonNode.Parse(File.ReadAllText(Path.Combine(rootA, "settings.json"))) as JsonObject;
            Check("U6", "kept: chats (codex.db, Sessions) — gone: models, skills, studio, logs, chrome, the backup, an unknown file",
                File.Exists(Path.Combine(rootA, "codex.db")) && File.Exists(Path.Combine(rootA, "Sessions", "s1.json"))
                && !Directory.Exists(Path.Combine(rootA, "Models")) && !Directory.Exists(Path.Combine(rootA, "llama-backend"))
                && !Directory.Exists(Path.Combine(rootA, "skills")) && !Directory.Exists(Path.Combine(rootA, "UiStudio"))
                && !Directory.Exists(Path.Combine(rootA, "logs")) && !Directory.Exists(Path.Combine(rootA, "chrome"))
                && !File.Exists(Path.Combine(rootA, "settings.json.bak")) && !File.Exists(Path.Combine(rootA, "brand-new-thing.dat")),
                string.Join(", ", Directory.EnumerateFileSystemEntries(rootA).Select(Path.GetFileName)));
            Check("U7", "settings unticked, license ticked → settings.json holds only the license and account",
                settingsA != null && settingsA["ActivationKey"]?.GetValue<string>() == "TEST-KEY-1234" && settingsA["AccountLinked"]?.GetValue<bool>() == true
                && !settingsA.ContainsKey("Theme") && !settingsA.ContainsKey("ModelDirectory"), settingsA?.ToJsonString() ?? "null");
            Check("U8", "a junction inside a deleted folder is unlinked — the folder it pointed at is untouched",
                File.Exists(Path.Combine(victimA, "precious.txt")) && !Directory.Exists(Path.Combine(rootA, "Cache")));
            Check("U9", "the bytes freed are counted, and nothing was left", cA.BytesFreed >= 3000 && cA.Problems.Count == 0,
                string.Join(" | ", cA.Problems.Select(p => p.Path + ": " + p.Message)));

            // ── B: keep nothing ──
            var (rootB, victimB) = MakeFakeRoot(work, "B");
            var cB = Clean(DataScan.Run(rootB), Array.Empty<KeepGroupId>(), Array.Empty<string>());
            cB.RemoveDataRootIfEmpty();
            Check("U10", "nothing ticked → the whole data folder is gone, the junction's target is not",
                !Directory.Exists(rootB) && File.Exists(Path.Combine(victimB, "precious.txt")));

            // ── C: keep settings, not the license ──
            var (rootC, _) = MakeFakeRoot(work, "C");
            Clean(DataScan.Run(rootC), new[] { KeepGroupId.Settings, KeepGroupId.Chats }, Array.Empty<string>());
            var settingsC = JsonNode.Parse(File.ReadAllText(Path.Combine(rootC, "settings.json"))) as JsonObject;
            Check("U11", "settings ticked, license unticked → the key and the account are stripped, the theme stays; the backup (plain-text key) is gone",
                settingsC != null && settingsC["Theme"]?.GetValue<string>() == "NeonCyber" && !settingsC.ContainsKey("ActivationKey")
                && !settingsC.ContainsKey("AccountEmailMasked") && !File.Exists(Path.Combine(rootC, "settings.json.bak"))
                && File.Exists(Path.Combine(rootC, "brand-new-thing.dat")), settingsC?.ToJsonString() ?? "null");

            // ── D: a locked file + a folder the user chose ──
            var (rootD, _) = MakeFakeRoot(work, "D");
            string models = Path.Combine(work, "D-my-models");
            Directory.CreateDirectory(models);
            File.WriteAllBytes(Path.Combine(models, "big.gguf"), new byte[5000]);
            var sD = JsonNode.Parse(File.ReadAllText(Path.Combine(rootD, "settings.json")))!.AsObject();
            sD["ModelDirectory"] = models;
            File.WriteAllText(Path.Combine(rootD, "settings.json"), sD.ToJsonString());
            var scanD = DataScan.Run(rootD);
            Check("U12", "a model folder chosen outside the data folder is its own row, with its size",
                scanD.Custom.Count == 1 && scanD.Custom[0].Kind == KeepGroupId.Models && scanD.CustomBytes[models] == 5000);
            UninstallCleaner cD;
            using (new FileStream(Path.Combine(rootD, "logs", "busy.log"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                cD = new UninstallCleaner(scanD, new[] { KeepGroupId.Chats }, new[] { models }, null, isolated: true, new UninstallReport());
                foreach (var g in cD.GroupsToDelete()) cD.DeleteGroup(g);
                foreach (var c in scanD.Custom) if (c.Path != models) cD.DeleteCustom(c);
            }
            Check("U13", "a file another program holds is reported as in use; the rest of its folder is still deleted",
                cD.Problems.Count == 1 && cD.Problems[0].Kind == DeleteFailure.InUse && Path.GetFileName(cD.Problems[0].Path) == "busy.log"
                && !File.Exists(Path.Combine(rootD, "logs", "other.log")),
                string.Join(" | ", cD.Problems.Select(p => $"{p.Kind} {p.Path}")));
            Check("U14", "the ticked outside folder is kept", File.Exists(Path.Combine(models, "big.gguf")));
            var cD2 = new UninstallCleaner(DataScan.Run(rootD), new[] { KeepGroupId.Chats }, Array.Empty<string>(), null, isolated: true, new UninstallReport());
            cD2.DeleteCustom(scanD.Custom[0]);
            Check("U15", "unticked, it is deleted", !Directory.Exists(models));

            // ── E: the owner's real registry and BrainX entries are not this fake folder's ──
            const string hostKey = @"Software\Google\Chrome\NativeMessagingHosts\com.xmanstudio.cluadex_chrome";
            string? hostBefore = Registry.CurrentUser.OpenSubKey(hostKey)?.GetValue(null) as string;
            string? vault = CluadeX.Services.Connections.BrainXBridges.ResolveVault();
            string? bridges = vault != null ? CluadeX.Services.Connections.BrainXBridges.FileFor(vault) : null;
            string? bridgesHash = bridges != null && File.Exists(bridges) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bridges))) : null;
            var (rootE, _) = MakeFakeRoot(work, "E");
            var cE = new UninstallCleaner(DataScan.Run(rootE), Array.Empty<KeepGroupId>(), Array.Empty<string>(), null, isolated: true, new UninstallReport());
            int removed = cE.RemoveLinks();
            string? hostAfter = Registry.CurrentUser.OpenSubKey(hostKey)?.GetValue(null) as string;
            string? bridgesHashAfter = bridges != null && File.Exists(bridges) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bridges))) : null;
            Check("U16", "a run on another data folder leaves the real Chrome host key and BrainX bridges exactly as they were",
                hostBefore == hostAfter && bridgesHash == bridgesHashAfter && removed == 1 && !Directory.Exists(Path.Combine(rootE, "chrome")),
                $"host {hostBefore} → {hostAfter} · bridges {(bridgesHash == bridgesHashAfter ? "same" : "CHANGED")} · removed {removed}");

            // ── F: the Installed-apps row ──
            string inst = Path.Combine(work, "FakeInstall");
            string current = Path.Combine(inst, "current");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "CluadeX.exe"), "");
            File.WriteAllText(Path.Combine(inst, "Update.exe"), "");
            File.WriteAllText(Path.Combine(current, "sq.version"),
                "<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd\"><metadata><id>CluadeXUninstallTest</id><version>9.9.9</version></metadata></package>");
            var detected = UninstallEntry.Detect(Path.Combine(current, "CluadeX.exe"));
            Check("U17", "a Velopack layout is recognised: package id, Update.exe, the command Windows will run",
                detected is { PackageId: "CluadeXUninstallTest" } && detected.KeyPath.EndsWith(@"\Uninstall\CluadeXUninstallTest")
                && detected.PageCommand == $"\"{Path.Combine(current, "CluadeX.exe")}\" --uninstall"
                && UninstallEntry.Detect(Path.Combine(work, "loose", "CluadeX.exe")) == null);

            // the row Windows' Installed apps shows: pointed at the page only when it is THIS install's
            string keyName = "CluadeXUninstallTest-" + Guid.NewGuid().ToString("N")[..6];
            string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName;
            var fakeInst = detected! with { PackageId = keyName };
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    k.SetValue("InstallLocation", inst);
                    k.SetValue("UninstallString", fakeInst.VelopackCommand);
                    k.SetValue("QuietUninstallString", fakeInst.VelopackCommand + " --silent");
                }
                string r1 = UninstallEntry.EnsurePointsAtPage(fakeInst);
                string r2 = UninstallEntry.EnsurePointsAtPage(fakeInst);
                string now, quiet;
                using (var k = Registry.CurrentUser.OpenSubKey(keyPath)) { now = k?.GetValue("UninstallString") as string; quiet = k?.GetValue("QuietUninstallString") as string; }
                var other = fakeInst with { Root = Path.Combine(work, "SomewhereElse") };
                string r3 = UninstallEntry.EnsurePointsAtPage(other);
                UninstallEntry.RestoreVelopackCommand(fakeInst);
                string restored;
                using (var k = Registry.CurrentUser.OpenSubKey(keyPath)) restored = k?.GetValue("UninstallString") as string;
                Check("U18", "Installed apps → the page (once; the silent command stays Velopack's); another copy's row is left alone; the fallback puts Velopack's back",
                    now == fakeInst.PageCommand && quiet == fakeInst.VelopackCommand + " --silent" && r1.StartsWith("now") && r2.StartsWith("already")
                    && r3.Contains("another copy") && restored == fakeInst.VelopackCommand, $"{r1} | {r2} | {r3} | {now}");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }

            string liveHost = Registry.CurrentUser.OpenSubKey(hostKey)?.GetValue(null) as string;
            bool removedLive = CluadeX.Services.Chrome.ChromeBridgeInstall.UnregisterIfFor(inst);
            Check("U19", "Velopack's uninstall hook of another copy leaves the Chrome host of the copy in use alone",
                !removedLive && liveHost == Registry.CurrentUser.OpenSubKey(hostKey)?.GetValue(null) as string, $"live host: {liveHost ?? "(none on this PC)"}");

            // ── G: v3.0.275 on the owner's PC freed 0 bytes — the data folder IS ~/.cluadex and the turn folders sit in
            //    %TEMP%, both protected, so every delete was refused. Decisions only against the real paths (nothing deleted). ──
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string realData = Path.Combine(profile, ".cluadex");
            string temp = Path.GetTempPath();
            string?[] allowed =
            {
                SafeDelete.CheckInside(Path.Combine(realData, "logs"), realData),
                SafeDelete.CheckInside(Path.Combine(realData, "settings.json.bak"), realData),
                SafeDelete.CheckInside(Path.Combine(realData, "Models", "m.gguf"), realData),
                SafeDelete.CheckInside(Path.Combine(temp, "cluadex-turn-1"), temp),
            };
            Check("U20", "inside ~/.cluadex and %TEMP% (protected themselves) a CluadeX item may be deleted",
                allowed.All(a => a == null) && ProtectedPaths.WhyProtected(realData) != null && ProtectedPaths.WhyProtected(temp) != null,
                string.Join(" | ", allowed.Select(a => a ?? "ok")));
            string?[] refusedAll =
            {
                SafeDelete.CheckInside(realData, realData),                                 // the owner itself
                SafeDelete.CheckInside(temp, temp),
                SafeDelete.CheckInside(Path.Combine(profile, "some-folder"), profile),      // the profile holds .ssh, Documents …
                SafeDelete.CheckInside(Path.Combine(profile, ".ssh"), profile),
                SafeDelete.CheckInside(Path.Combine(realData, "..", ".ssh"), realData),     // climbs out
                SafeDelete.CheckInside(Path.Combine(profile, "Documents", "x"), realData),  // outside the owner
                SafeDelete.CheckInside(Path.Combine(Path.GetPathRoot(temp)!, "cluadex-x"), Path.GetPathRoot(temp)),   // a drive as owner
                SafeDelete.CheckInside(Path.Combine(realData, "logs"), ""),
            };
            Check("U21", "never: the owner itself, the profile or a drive as owner, a protected folder, a path outside or climbing out",
                refusedAll.All(r => r != null), string.Join(" | ", refusedAll.Select(r => r ?? "ALLOWED")));
            Check("U22", "the app-wide rule (SafeDelete.Check) is unchanged: it still refuses a protected owner",
                SafeDelete.Check(Path.Combine(realData, "logs"), realData) != null && SafeDelete.Check(Path.Combine(temp, "cluadex-turn-1"), temp) != null);

            // the same, deleting for real: a fake data folder registered as protected stands in for ~/.cluadex
            var (rootG, victimG) = MakeFakeRoot(work, "G");
            UninstallCleaner cG;
            bool rootRefused, nestedRefused;
            string nested = Path.Combine(rootG, "plugins", "p");
            using (ProtectedPaths.ProtectForTest(rootG))
            {
                cG = Clean(DataScan.Run(rootG), new[] { KeepGroupId.Settings, KeepGroupId.License, KeepGroupId.Chats }, Array.Empty<string>());
                var self = new TreeDeleter(rootG);
                rootRefused = !self.Delete(rootG) && self.Problems.Count == 1 && Directory.Exists(rootG);
                Directory.CreateDirectory(Path.Combine(nested, "own"));
                using (ProtectedPaths.ProtectForTest(Path.Combine(nested, "own")))
                {
                    var holder = new TreeDeleter(rootG);
                    nestedRefused = !holder.Delete(Path.Combine(rootG, "plugins")) && Directory.Exists(Path.Combine(nested, "own"));
                }
            }
            Check("U23", "data folder protected (as ~/.cluadex is): the unticked rows are deleted, the ticked kept, nothing refused",
                cG.Problems.Count == 0 && cG.BytesFreed >= 3000 && !Directory.Exists(Path.Combine(rootG, "Models"))
                && !Directory.Exists(Path.Combine(rootG, "logs")) && !Directory.Exists(Path.Combine(rootG, "chrome"))
                && File.Exists(Path.Combine(rootG, "codex.db")) && File.Exists(Path.Combine(rootG, "settings.json"))
                && File.Exists(Path.Combine(victimG, "precious.txt")),
                string.Join(" | ", cG.Problems.Select(p => p.Path + ": " + p.Message)));
            Check("U24", "…but the data folder itself is refused, and so is a folder that holds a protected one", rootRefused && nestedRefused);

            string tempItem = Path.Combine(temp, "cluadex-uninstalltest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(tempItem, "hooks"));
            File.WriteAllText(Path.Combine(tempItem, "hooks", "h.json"), "{}");
            var tempDel = new TreeDeleter(temp);
            bool tempGone = tempDel.Delete(tempItem) && !Directory.Exists(tempItem);
            try { if (Directory.Exists(tempItem)) Directory.Delete(tempItem, true); } catch { }
            Check("U25", "a %TEMP%\\cluadex-* turn folder is deleted (%TEMP% itself is protected)", tempGone && tempDel.Problems.Count == 0 && Directory.Exists(temp),
                string.Join(" | ", tempDel.Problems.Select(p => p.Message)));

            // ── H: a reinstall after an uninstall that kept the settings opens the setup wizard ──
            var sC = JsonNode.Parse(File.ReadAllText(Path.Combine(rootC, "settings.json"))) as JsonObject;
            var sG = JsonNode.Parse(File.ReadAllText(Path.Combine(rootG, "settings.json"))) as JsonObject;
            var cNot = new UninstallCleaner(DataScan.Run(rootA), new[] { KeepGroupId.License }, Array.Empty<string>(), null, isolated: true, new UninstallReport());
            Check("U26", "settings kept → the wizard flag is reset, the rest stays; license only → no flag at all; settings not kept → untouched",
                sC?["SetupWizardCompleted"]?.GetValue<bool>() == false && sC["Theme"]?.GetValue<string>() == "NeonCyber"
                && sG?["SetupWizardCompleted"]?.GetValue<bool>() == false && sG["ActivationKey"]?.GetValue<string>() == "TEST-KEY-1234"
                && settingsA != null && !settingsA.ContainsKey("SetupWizardCompleted") && !cNot.ResetSetupWizard(),
                $"{sC?.ToJsonString()} · {sG?.ToJsonString()}");
            Check("U27", "the wizard opens: fresh install, Velopack's first run even with settings kept and marked done, --setup",
                SetupWizardGate.Show(false, false, true, false) && SetupWizardGate.Show(false, true, false, true)
                && SetupWizardGate.Show(false, false, false, true) && SetupWizardGate.Show(true, true, false, false));
            Check("U28", "…and never on the restart after an auto-update, nor on an old install that already had settings",
                !SetupWizardGate.Show(false, true, false, false) && !SetupWizardGate.Show(false, false, false, false)
                && !SetupWizardGate.FreshInstall(true, false, false));
        }
        finally
        {
            try { foreach (var j in Directory.EnumerateDirectories(work, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList()) Directory.Delete(j, false); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }
    }

    static UninstallCleaner Clean(DataScan scan, IEnumerable<KeepGroupId> keep, IEnumerable<string> keepCustom)
    {
        var c = new UninstallCleaner(scan, keep, keepCustom, null, isolated: true, new UninstallReport());
        foreach (var g in c.GroupsToDelete()) c.DeleteGroup(g);
        c.ResetSetupWizard();
        c.RemoveLinks();
        return c;
    }

    /// <summary>A small ~/.cluadex with one of everything, plus a junction (Cache\link) to a folder outside it.</summary>
    static (string Root, string Victim) MakeFakeRoot(string work, string tag)
    {
        string root = Path.Combine(work, tag + "-cluadex");
        string victim = Path.Combine(work, tag + "-victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "precious.txt"), "not CluadeX's");
        void F(string rel, int size = 10) { string p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, new byte[size]); }
        var settings = new JsonObject
        {
            ["Theme"] = "NeonCyber", ["Language"] = "th", ["ModelDirectory"] = Path.Combine(root, "Models"),
            ["ActivationKey"] = "TEST-KEY-1234", ["LicenseType"] = "lifetime", ["AccountLinked"] = true, ["AccountEmailMasked"] = "te***@example.com", ["SetupWizardCompleted"] = true,
        };
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), settings.ToJsonString());
        File.WriteAllText(Path.Combine(root, "settings.json.bak"), settings.ToJsonString());
        F("codex.db", 200); F("Sessions/s1.json"); F("checkpoints/c1");
        F("Models/m.gguf", 3000); F("llama-backend/ggml.dll", 100);
        F("skills/s/SKILL.md"); F("plugins/p/x.js"); F("mcp_servers.json");
        F("UiStudio/page.json"); F("logs/busy.log"); F("logs/other.log"); F("crash-logs/c.txt");
        F("chrome/extension/manifest.json"); F("brand-new-thing.dat");
        Directory.CreateDirectory(Path.Combine(root, "Cache"));
        var link = Path.Combine(root, "Cache", "link");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{victim}\"") { CreateNoWindow = true, UseShellExecute = false });
        mk!.WaitForExit();
        return (root, victim);
    }
}
