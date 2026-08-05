using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services;

/// <summary>
/// Remembers, across restarts, that an update was attempted and did not take.
///
/// WHY THIS HAS TO BE ON DISK. Applying an update ends the process, so any
/// in-memory guard dies with the attempt it was guarding. BrainX learned this the
/// expensive way on 2026-08-01: a handle on its `current` directory that Velopack
/// could not rename made every apply fail, and the app relaunched → checked →
/// downloaded → applied → failed → relaunched. **Four PIDs in 75 seconds**, with
/// no counter anywhere able to notice it was the same failure over and over.
///
/// A counter is only a counter if it outlives the thing it is counting.
///
/// Failure is INFERRED, never reported — nothing of ours survives to write "that
/// didn't work". So an attempt is recorded and flushed BEFORE the process hands
/// itself to the updater, and the next launch decides: came back on the old
/// version ⇒ the attempt failed; came back as the target ⇒ it worked, wipe the
/// slate.
/// </summary>
public sealed class UpdateAttemptLog
{
    /// <summary>Give up after this many consecutive failures at the SAME version and
    /// let the user apply manually. Three rides out a transient lock while still
    /// turning a permanent one into a message instead of a machine that will not
    /// stay open.</summary>
    public const int MaxConsecutiveFailures = 3;

    public string? TargetVersion { get; set; }
    public int Failures { get; set; }
    public DateTime? FirstAttemptUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }

    [JsonIgnore] private string _path = "";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static UpdateAttemptLog Load(string dir)
    {
        var path = Path.Combine(dir, "update-attempts.json");
        try
        {
            if (File.Exists(path))
            {
                var log = JsonSerializer.Deserialize<UpdateAttemptLog>(File.ReadAllText(path));
                if (log != null) { log._path = path; return log; }
            }
        }
        catch { /* a corrupt log must never stop the app updating */ }
        return new UpdateAttemptLog { _path = path };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* best effort — never block an update on bookkeeping */ }
    }

    /// <summary>
    /// Call once at startup with the version actually running. If it matches the
    /// version we were trying to reach, the update landed and the history is
    /// cleared. Anything still recorded after this is a genuine failure.
    /// </summary>
    public void NoteRunningVersion(string current)
    {
        if (string.IsNullOrEmpty(TargetVersion)) return;
        if (!VersionsMatch(TargetVersion, current)) return;
        TargetVersion = null; Failures = 0; FirstAttemptUtc = null; LastAttemptUtc = null;
        Save();
    }

    /// <summary>True when this exact version has already failed too many times and
    /// must not be applied again without the user asking.</summary>
    public bool ShouldStopTrying(string target) =>
        TargetVersion != null
        && VersionsMatch(TargetVersion, target)
        && Failures >= MaxConsecutiveFailures;

    /// <summary>
    /// Record an attempt. MUST be called — and flushed — before handing control to
    /// the updater, because nothing of ours runs afterwards to record it.
    /// </summary>
    public void RecordAttempt(string target)
    {
        if (TargetVersion == null || !VersionsMatch(TargetVersion, target))
        {
            TargetVersion = target; Failures = 0; FirstAttemptUtc = DateTime.UtcNow;
        }
        Failures++;
        LastAttemptUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>Human-readable state for the update UI, or null when there is
    /// nothing worth saying.</summary>
    public string? Describe() =>
        TargetVersion == null || Failures == 0 ? null
        : Failures >= MaxConsecutiveFailures
            ? $"v{TargetVersion} failed to apply {Failures}× — automatic install is paused. Use “Restart & apply” to try manually."
            : $"v{TargetVersion} did not apply on the last attempt ({Failures}/{MaxConsecutiveFailures}).";

    // Velopack reports "3.0.52" while the assembly carries "3.0.52+fd40d8d";
    // compare on the numeric triple only.
    private static bool VersionsMatch(string a, string b) =>
        Trim(a).Equals(Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string v)
    {
        var s = v.TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        return cut > 0 ? s[..cut] : s;
    }
}
