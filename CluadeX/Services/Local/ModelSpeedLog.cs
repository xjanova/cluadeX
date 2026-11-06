using System.IO;
using System.Text.Json;

namespace CluadeX.Services.Local;

/// <summary>Why a model was judged too slow for agent work on this PC.</summary>
public enum SlowReason
{
    /// <summary>Measured fast enough (the record only keeps the numbers).</summary>
    None,
    /// <summary>Writes too few tokens per second (<see cref="ModelSpeedLog.SlowGenTps"/>).</summary>
    SlowGeneration,
    /// <summary>Reads the prompt so slowly that an agent prompt takes minutes before the first word
    /// (<see cref="ModelSpeedLog.SlowPromptTps"/>).</summary>
    SlowPrompt,
    /// <summary>One step took longer than <see cref="ModelSpeedLog.SlowStepSec"/>.</summary>
    SlowStep,
    /// <summary>No answer at all within the run's time budget.</summary>
    NoAnswer,
}

/// <summary>The last speed measured for a model on a graphics card, and the verdict.</summary>
public sealed record ModelSpeed(
    string Path,
    long FileSize,
    string Gpu,
    DateTime AtUtc,
    double PromptTps,
    double GenTps,
    int PromptTokens,
    int GenTokens,
    double StepSec,
    SlowReason Reason,
    /// <summary>Measured on tuned settings (a profile for this card existed, or this IS the tune's result). Only then is a
    /// slow measurement a verdict; on untuned settings it is a "tune it first" suggestion.</summary>
    bool Tuned = false,
    /// <summary>What the tune tried and reached, one line per candidate ("" when there was no tune).</summary>
    string Tried = "")
{
    /// <summary>The "ช้าบนเครื่องนี้" flag: slow even at the best settings found for this card.</summary>
    public bool IsSlow => Reason != SlowReason.None && Tuned;
    /// <summary>Slow at whatever settings it ran with, never tuned: suggest tuning, do not flag.</summary>
    public bool NeedsTune => Reason != SlowReason.None && !Tuned;
}

/// <summary>
/// Which local models are too slow for agent work on this PC, measured from real runs (owner 2026-11-06: "มาร์คว่ามัน
/// ใช้ได้ไม่ดี … ให้ cluadex รู้เรื่องพวกนี้ ควรตัดสินใจอะไร"). Seen that day: Qwen3-Coder-30B-A3B Q4_K_M on an 8 GB
/// GTX 1070 Ti, partly on the CPU, gave no answer at all in 5 minutes to a 12k-token agent prompt.
///
/// Unlike <see cref="ModelHealthLog"/> nothing is blocked: plain chat may be fine on a slow model. The verdict is shown
/// (a warning pill on the Models card, a warning before an agent run) with the numbers and what to do instead, and it
/// lifts itself: a later run on the same card that measures acceptable speed clears it, a different card ignores it
/// (it is kept per GPU fingerprint), and Force load / the pill's × forget it.
///
/// Thresholds (an agent step = read a ~8–12k-token prompt, write a tool call of a few hundred tokens):
/// <list type="bullet">
/// <item>generation below <see cref="SlowGenTps"/> tok/s (over at least <see cref="MinGenSample"/> tokens): a 400-token
/// file edit takes over 1½ minutes;</item>
/// <item>prompt reading below <see cref="SlowPromptTps"/> tok/s (over at least <see cref="MinPromptSample"/> tokens):
/// an 8k agent prompt takes over 3 minutes before the first word;</item>
/// <item>a step longer than <see cref="SlowStepSec"/> s, or no answer at all in the run's budget.</item>
/// </list>
/// </summary>
public sealed class ModelSpeedLog
{
    public const double SlowGenTps = 4.0;
    public const int MinGenSample = 32;
    public const double SlowPromptTps = 45.0;
    public const int MinPromptSample = 1000;
    public const double SlowStepSec = 180;

    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, ModelSpeed>? _entries;

    public ModelSpeedLog(string file) => _file = file;

    private static ModelSpeedLog? _default;
    /// <summary>The log of this install (follows portable.txt like every other data file).</summary>
    public static ModelSpeedLog Default => _default ??= new ModelSpeedLog(
        System.IO.Path.Combine(SettingsService.ResolveDataRoot(out _), "model-speed.json"));

    public string FilePath => _file;

    /// <summary>The verdict for one measured step (pure, so the thresholds are testable).</summary>
    public static SlowReason Judge(int promptTokens, double promptTps, int genTokens, double genTps, double stepSec)
    {
        if (genTokens >= MinGenSample && genTps > 0 && genTps < SlowGenTps) return SlowReason.SlowGeneration;
        if (promptTokens >= MinPromptSample && promptTps > 0 && promptTps < SlowPromptTps) return SlowReason.SlowPrompt;
        if (stepSec > SlowStepSec) return SlowReason.SlowStep;
        return SlowReason.None;
    }

    /// <summary>A step finished: keep its numbers. A slow step sets the verdict (a flag when <paramref name="tuned"/>, a "tune
    /// it" suggestion otherwise); an acceptable one clears it — but only a step big enough to tell (a 30-token answer to a
    /// 200-token prompt says nothing about an agent prompt). A slow untuned step never replaces a flag set after a tune.
    /// Returns the record now kept (null when the step was too small to judge and nothing was kept before).</summary>
    public ModelSpeed? Observe(string modelPath, string gpu, int promptTokens, double promptTps, int genTokens, double genTps, double stepSec,
        bool tuned = false)
    {
        var reason = Judge(promptTokens, promptTps, genTokens, genTps, stepSec);
        bool tellsEnough = reason != SlowReason.None || genTokens >= MinGenSample || promptTokens >= MinPromptSample;
        lock (_gate)
        {
            var all = Load();
            string key = Key(modelPath);
            all.TryGetValue(key, out var old);
            if (!tellsEnough) return old;
            if (reason != SlowReason.None && !tuned && old is { IsSlow: true } && SameCard(old, gpu)) return old;
            var rec = new ModelSpeed(modelPath, SizeOf(modelPath), gpu ?? "", DateTime.UtcNow,
                Math.Round(promptTps, 1), Math.Round(genTps, 1), promptTokens, genTokens, Math.Round(stepSec, 1), reason,
                tuned, reason != SlowReason.None && old is { Tuned: true } && SameCard(old, gpu) ? old.Tried : "");
            all[key] = rec;
            Save(all);
            return rec;
        }
    }

    /// <summary>The run gave up waiting: no answer in <paramref name="waitedSec"/> seconds (a flag only when tuned).</summary>
    public ModelSpeed RecordNoAnswer(string modelPath, string gpu, double waitedSec, int promptTokens, bool tuned = false)
    {
        lock (_gate)
        {
            var all = Load();
            all.TryGetValue(Key(modelPath), out var old);
            if (!tuned && old is { IsSlow: true } && SameCard(old, gpu)) return old;
            var rec = new ModelSpeed(modelPath, SizeOf(modelPath), gpu ?? "", DateTime.UtcNow, 0, 0, promptTokens, 0,
                Math.Round(waitedSec, 1), SlowReason.NoAnswer, tuned, old is { Tuned: true } ? old.Tried : "");
            all[Key(modelPath)] = rec;
            Save(all);
            return rec;
        }
    }

    /// <summary>The tune's verdict. Passed: the best numbers, not slow (clears a flag or a suggestion). Failed: the flag,
    /// with what was tried — the only way an untuned model becomes "slow here".</summary>
    public ModelSpeed RecordTune(string modelPath, string gpu, TuneBench? best, bool passed, string tried)
    {
        lock (_gate)
        {
            var all = Load();
            SlowReason reason = SlowReason.None;
            if (!passed)
            {
                reason = best is { Ok: true } b ? Judge(b.PromptN, b.PromptTps, b.GenN, b.GenTps, 0) : SlowReason.NoAnswer;
                if (reason == SlowReason.None) reason = SlowReason.SlowGeneration;
            }
            var rec = new ModelSpeed(modelPath, SizeOf(modelPath), gpu ?? "", DateTime.UtcNow,
                Math.Round(best?.PromptTps ?? 0, 1), Math.Round(best?.GenTps ?? 0, 1), best?.PromptN ?? 0, best?.GenN ?? 0,
                0, reason, Tuned: true, Tried: tried);
            all[Key(modelPath)] = rec;
            Save(all);
            return rec;
        }
    }

    private static bool SameCard(ModelSpeed rec, string? gpu)
        => rec.Gpu.Length == 0 || string.IsNullOrEmpty(gpu) || string.Equals(rec.Gpu, gpu, StringComparison.OrdinalIgnoreCase);

    /// <summary>A slow measurement on untuned settings, for this file and card: "tune it first".</summary>
    public ModelSpeed? FindNeedsTune(string? modelPath, string? currentGpu)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        lock (_gate)
        {
            if (!Load().TryGetValue(Key(modelPath), out var rec) || !rec.NeedsTune) return null;
            if (rec.FileSize != SizeOf(modelPath) || !SameCard(rec, currentGpu)) return null;
            return rec;
        }
    }

    /// <summary>The slow verdict that applies now: same file (path + size) and same graphics card. A card change means the
    /// old numbers say nothing (empty fingerprints on either side = unknown, the verdict still applies).</summary>
    public ModelSpeed? FindSlow(string? modelPath, string? currentGpu)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        lock (_gate)
        {
            if (!Load().TryGetValue(Key(modelPath), out var rec) || !rec.IsSlow) return null;
            if (rec.FileSize != SizeOf(modelPath) || !SameCard(rec, currentGpu)) return null;
            return rec;
        }
    }

    public ModelSpeed? Find(string modelPath)
    {
        lock (_gate) return Load().TryGetValue(Key(modelPath), out var rec) ? rec : null;
    }

    public bool Clear(string modelPath)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(Key(modelPath))) return false;
            Save(all);
            return true;
        }
    }

    // ─── telling the owner ───

    /// <summary>The numbers, short: "2.1 tok/s เขียน · 38 tok/s อ่าน prompt".</summary>
    public static string Numbers(ModelSpeed s, bool isThai)
    {
        var parts = new List<string>();
        if (s.Reason == SlowReason.NoAnswer)
            parts.Add(isThai ? $"ไม่ตอบเลยใน {Duration(s.StepSec, true)} (prompt ≈ {s.PromptTokens:N0} token)"
                             : $"no answer in {Duration(s.StepSec, false)} (prompt ≈ {s.PromptTokens:N0} tokens)");
        else
        {
            if (s.GenTps > 0) parts.Add(isThai ? $"เขียน {s.GenTps:0.#} tok/s" : $"writes {s.GenTps:0.#} tok/s");
            if (s.PromptTps > 0) parts.Add(isThai ? $"อ่าน prompt {s.PromptTps:0.#} tok/s" : $"reads the prompt at {s.PromptTps:0.#} tok/s");
            if (s.StepSec > 0) parts.Add(isThai ? $"ขั้นละ {Duration(s.StepSec, true)}" : $"{Duration(s.StepSec, false)} per step");
        }
        return string.Join(" · ", parts);
    }

    public static string ReasonText(SlowReason r, bool isThai) => (r, isThai) switch
    {
        (SlowReason.SlowGeneration, true) => "เขียนคำตอบช้ามาก",
        (SlowReason.SlowGeneration, false) => "it writes very slowly",
        (SlowReason.SlowPrompt, true) => "อ่าน prompt ช้ามาก (งาน agent ต้องรอหลายนาทีก่อนคำแรก)",
        (SlowReason.SlowPrompt, false) => "it reads the prompt very slowly (minutes before the first word of an agent step)",
        (SlowReason.SlowStep, true) => "แต่ละขั้นใช้เวลานานเกินไป",
        (SlowReason.SlowStep, false) => "each step takes too long",
        (SlowReason.NoAnswer, true) => "ไม่ตอบเลยภายในเวลาที่ให้",
        (SlowReason.NoAnswer, false) => "it gave no answer in the time allowed",
        _ => "",
    };

    /// <summary>The tooltip / Models card text: the flag (after a tune, with what was tried) or the "tune it" suggestion.</summary>
    public static string Describe(ModelSpeed s, bool isThai)
    {
        string when = s.AtUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(isThai ? "th-TH" : "en-US"));
        string on = s.Gpu.Length > 0 ? (isThai ? $" บน {s.Gpu}" : $" on {s.Gpu}") : "";
        if (s.NeedsTune)
            return isThai
                ? $"ยังไม่ได้ปรับค่า — รอบที่รันช้า ({ReasonText(s.Reason, true)}) เมื่อ {when}{on}: {Numbers(s, true)}\n"
                  + "ค่าที่ใช้ตอนนั้นอาจไม่เหมาะกับเครื่องนี้ กด \"ปรับค่าให้เหมาะ\" ให้ CluadeX ลองหาค่าที่ดีที่สุดก่อนตัดสิน"
                : $"Not tuned yet — a run was slow ({ReasonText(s.Reason, false)}) on {when}{on}: {Numbers(s, false)}\n"
                  + "The settings it used may not suit this PC: press \"Tune for this PC\" to let CluadeX find the best ones first";
        string tried = s.Tried.Length > 0 ? (isThai ? $"\nลองแล้ว:\n{s.Tried}" : $"\nTried:\n{s.Tried}") : "";
        return isThai
            ? $"ช้าเกินไปสำหรับงาน agent บนเครื่องนี้ แม้ปรับค่าแล้ว — {ReasonText(s.Reason, true)}\nวัดเมื่อ {when}{on}: {Numbers(s, true)}{tried}\n"
              + "ยังโหลดและคุยธรรมดาได้ · ป้ายนี้หายเองเมื่อรอบหลังวัดได้เร็วพอ หรือเปลี่ยนการ์ดจอ"
            : $"Too slow for agent work on this PC, even tuned — {ReasonText(s.Reason, false)}\nMeasured {when}{on}: {Numbers(s, false)}{tried}\n"
              + "It still loads and chats · this goes away when a later run measures fast enough, or with another graphics card";
    }

    /// <summary>Said before an agent run on a model that ran slowly on untuned settings.</summary>
    public static string NeedsTuneWarning(ModelSpeed s, string modelName, bool isThai)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(modelName);
        return isThai
            ? $"🔧 {name} เคยรันช้าบนเครื่องนี้ ({Numbers(s, true)}) แต่ยังไม่ได้ปรับค่า — ลองกด \"ปรับค่าให้เหมาะ\" ในหน้า Models ก่อน (เช่น ให้ expert ของ MoE ไปอยู่ CPU, ลด ctx, KV q8)"
            : $"🔧 {name} ran slowly on this PC ({Numbers(s, false)}) but was never tuned — try \"Tune for this PC\" on the Models page first (MoE experts on the CPU, a smaller ctx, a q8 KV cache)";
    }

    /// <summary>Said in the chat before an agent run on a model that was too slow here — with what to do instead.</summary>
    public static string PreRunWarning(ModelSpeed s, string modelName, bool escalationOn, bool isThai)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(modelName);
        if (isThai)
            return $"🐢 {name} ช้าเกินไปสำหรับงาน agent บนเครื่องนี้แม้ปรับค่าแล้ว ({Numbers(s, true)}) — งานนี้อาจช้ามากหรือไม่เสร็จ\n"
                + (escalationOn ? "ถ้าติด จะส่งต่อให้โมเดลที่แรงกว่าตามที่ตั้งไว้ · " : "")
                + "ทางเลือก: โมเดล local ที่เล็กพอให้อยู่ใน VRAM ทั้งหมด (ป้าย Fit ในหน้า Models), ลด Context Size, หรือใช้โมเดล API";
        return $"🐢 {name} is too slow for agent work on this PC even tuned ({Numbers(s, false)}) — this task may be very slow or not finish.\n"
            + (escalationOn ? "If it stalls, the stronger model you set takes over · " : "")
            + "Options: a local model small enough to sit fully in VRAM (Fit badge on the Models page), a lower Context Size, or an API model";
    }

    private static string Duration(double sec, bool isThai) => sec >= 120
        ? (isThai ? $"{sec / 60:0.#} นาที" : $"{sec / 60:0.#} min")
        : (isThai ? $"{sec:0} วินาที" : $"{sec:0} s");

    // ─── storage (same shape as ModelHealthLog) ───

    private Dictionary<string, ModelSpeed> Load()
    {
        if (_entries != null) return _entries;
        _entries = new Dictionary<string, ModelSpeed>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_file))
            {
                var list = JsonSerializer.Deserialize<List<ModelSpeed>>(File.ReadAllText(_file));
                if (list != null)
                    foreach (var s in list.Where(s => !string.IsNullOrEmpty(s.Path)))
                        _entries[Key(s.Path)] = s;
            }
        }
        catch { /* a damaged file only loses the measurements */ }
        return _entries;
    }

    private void Save(Dictionary<string, ModelSpeed> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static string Key(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }
}
