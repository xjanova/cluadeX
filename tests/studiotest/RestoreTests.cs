using System.IO;
using CluadeX.Services;
using CluadeX.Services.UiStudio;

namespace StudioTest;

/// <summary>Undo All after the AI moved a folder (2026-11-03): `git mv lib/screens lib/pages` leaves the snapshot's
/// files under a folder that no longer exists and the moved copies marked as new. The originals must come back and
/// the moved copies must never be deleted while an original could not be put back.</summary>
internal static partial class Program
{
    static void RestoreAfterFolderMove()
    {
        Console.WriteLine("Restore after a folder move");
        string root = Path.Combine(Path.GetTempPath(), "cx-restore-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string P(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(P("lib/pages"));
            File.WriteAllText(P("lib/pages/home.dart"), "class Home {}\n");
            File.WriteAllText(P("lib/pages/about.dart"), "class About {}\n");
            var svc = new StudioService(new SettingsService());

            var before = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [P("lib/screens/home.dart")] = "class Home {}\n",
                [P("lib/screens/about.dart")] = "class About {}\n",
                [P("lib/pages/home.dart")] = null,
                [P("lib/pages/about.dart")] = null,
            };
            var failed = svc.RestoreFiles(before);
            Check("restore: the moved-away folder is created again and its files get their text back",
                failed.Count == 0 && File.ReadAllText(P("lib/screens/home.dart")) == "class Home {}\n"
                && File.ReadAllText(P("lib/screens/about.dart")) == "class About {}\n", string.Join(" | ", failed));
            Check("restore: then the moved copies are removed", !File.Exists(P("lib/pages/home.dart")) && !File.Exists(P("lib/pages/about.dart")));

            // An original that cannot be written: its "folder" is a file. The moved copies are the only other copy.
            Directory.CreateDirectory(P("lib/pages"));
            File.WriteAllText(P("lib/pages/home.dart"), "class Home {}\n");
            File.WriteAllText(P("lib/blocked"), "a file where a folder should be");
            var stuck = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [P("lib/blocked/home.dart")] = "class Home {}\n",
                [P("lib/pages/home.dart")] = null,
            };
            failed = svc.RestoreFiles(stuck);
            Check("restore: when an original cannot be put back, nothing is deleted and both paths are reported",
                File.Exists(P("lib/pages/home.dart")) && failed.Count == 2, string.Join(" | ", failed));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
