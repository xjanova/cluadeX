using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Local;

/// <summary>Why a local model failed, as far as its error text and exit code tell.</summary>
public enum ModelFailureKind
{
    /// <summary>GPU or system memory ran out (cudaMalloc, "failed to allocate", vk OutOfDeviceMemory…).</summary>
    OutOfMemory,
    /// <summary>The backend process died (access violation, abort, killed) — at load or mid-answer.</summary>
    Crash,
    /// <summary>The file cannot be read as a model (bad magic, truncated download, tensor data out of range).</summary>
    Corrupt,
    /// <summary>The llama.cpp here does not know the model's architecture.</summary>
    Unsupported,
    /// <summary>The server never became ready in time.</summary>
    Timeout,
    /// <summary>Failed, reason not recognised — the backend's own words are kept.</summary>
    Unknown,
}

/// <summary>One model that failed on this PC.</summary>
public sealed record ModelFailure(
    string Path,
    long FileSize,
    DateTime AtUtc,
    ModelFailureKind Kind,
    string Detail,
    string Gpu,
    int Count,
    bool DuringRun);

/// <summary>A load refused because the model failed here before; the owner must press Force load.</summary>
public sealed class ModelBlockedException : Exception
{
    public ModelFailure Failure { get; }
    public string ShortMessage { get; }
    public ModelBlockedException(ModelFailure failure, string message, string shortMessage) : base(message)
    {
        Failure = failure;
        ShortMessage = shortMessage;
    }
}

/// <summary>A load that just failed, explained (what happened, why, what to do).</summary>
public sealed class ModelLoadFailedException : Exception
{
    public ModelFailure Failure { get; }
    public ModelLoadFailedException(ModelFailure failure, string message, Exception inner) : base(message, inner) => Failure = failure;
}

/// <summary>
/// Remembers local models that failed to load or died while answering, so CluadeX does not load them again by itself
/// (owner 2026-11-06: "ถ้าโมเดลไหนเคยรันแล้วไม่ผ่าน ก็จะไม่ให้โหลดอีก จนกว่าผู้ใช้จะไปกดปุ่ม force load อีกรอบ เพราะอาจมีการ
/// ลองเปลี่ยนการ์ดจอมา"). A second try of a model that ran the GPU out of memory can restart a whole PC (2026-09-24).
///
/// Keyed by path + file size (a re-downloaded file of another size is a new model). Saved in &lt;data root&gt;\model-health.json.
/// The GPU that failed is kept, so the refusal can say when the card has changed since — the owner still decides.
/// </summary>
public sealed class ModelHealthLog
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, ModelFailure>? _entries;

    public ModelHealthLog(string file) => _file = file;

    private static ModelHealthLog? _default;
    /// <summary>The log of this install (follows portable.txt like every other data file).</summary>
    public static ModelHealthLog Default => _default ??= new ModelHealthLog(
        System.IO.Path.Combine(SettingsService.ResolveDataRoot(out _), "model-health.json"));

    public string FilePath => _file;

    // ─── what failed ───

    public ModelFailure? Find(string modelPath)
    {
        lock (_gate)
        {
            var all = Load();
            return all.TryGetValue(Key(modelPath), out var f) && f.FileSize == SizeOf(modelPath) ? f : null;
        }
    }

    public IReadOnlyList<ModelFailure> All()
    {
        lock (_gate) return Load().Values.OrderByDescending(f => f.AtUtc).ToList();
    }

    /// <summary>Record a failure; returns the record (Count goes up on a repeat).</summary>
    public ModelFailure Record(string modelPath, ModelFailureKind kind, string detail, string gpu, bool duringRun)
    {
        lock (_gate)
        {
            var all = Load();
            string key = Key(modelPath);
            long size = SizeOf(modelPath);
            int count = all.TryGetValue(key, out var old) && old.FileSize == size ? old.Count + 1 : 1;
            var f = new ModelFailure(modelPath, size, DateTime.UtcNow, kind, Trim(detail, 600), gpu ?? "", count, duringRun);
            all[key] = f;
            Save(all);
            return f;
        }
    }

    /// <summary>A load that worked clears the model's record (the Force load that succeeded, a fixed backend…).</summary>
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

    // ─── a load the app did not live through ───
    // An in-process (LLamaSharp) crash, or a GPU fault that restarts the PC, ends CluadeX itself — nothing is left to
    // catch the error. A marker written before the load and removed after it is the only trace: found at the next load,
    // it becomes a Crash record for that model.

    private string MarkerFile => _file + ".loading";

    public void MarkLoading(string modelPath, string gpu)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(MarkerFile, JsonSerializer.Serialize(new LoadingMarker(modelPath, gpu ?? "", DateTime.UtcNow)));
        }
        catch { }
    }

    public void EndLoading()
    {
        try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); } catch { }
    }

    /// <summary>A load that was going on when CluadeX (or the PC) went down: recorded as a crash, marker removed.
    /// Null when the last load ended normally.</summary>
    public ModelFailure? TakeInterruptedLoad()
    {
        LoadingMarker? m = null;
        try
        {
            if (!File.Exists(MarkerFile)) return null;
            m = JsonSerializer.Deserialize<LoadingMarker>(File.ReadAllText(MarkerFile));
        }
        catch { }
        EndLoading();
        if (m == null || string.IsNullOrEmpty(m.Path)) return null;
        return Record(m.Path, ModelFailureKind.Crash,
            $"CluadeX or the PC went down while this model was loading ({m.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm})", m.Gpu, duringRun: false);
    }

    private sealed record LoadingMarker(string Path, string Gpu, DateTime AtUtc);

    // ─── reading an error ───

    private static readonly (ModelFailureKind Kind, Regex Pattern)[] Signs =
    {
        (ModelFailureKind.OutOfMemory, new Regex(@"out of memory|cudaMalloc failed|failed to allocate|unable to allocate|cannot allocate|" +
            @"OutOfDeviceMemory|ErrorOutOfHostMemory|insufficient memory|not enough memory|ggml_backend_\w*_buffer_type_alloc_buffer|" +
            @"CUBLAS_STATUS_ALLOC_FAILED|OutOfMemoryException|InsufficientMemoryException", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (ModelFailureKind.Unsupported, new Regex(@"unknown model architecture|unsupported model architecture|ModelArchitectureNotSupported",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (ModelFailureKind.Corrupt, new Regex(@"invalid magic|not a gguf|failed to read (the )?(header|tensor|key)|tensor .* data is not within|" +
            @"unexpectedly reached end of file|file is truncated|corrupt", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (ModelFailureKind.Timeout, new Regex(@"timed out|timeout|did not become ready", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        (ModelFailureKind.Crash, new Regex(@"crash|access violation|exited unexpectedly|stopped \(crashed|segmentation|abort|GGML_ASSERT|" +
            @"exit code", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    /// <summary>Windows exit codes that mean the process died, not that it quit.</summary>
    public static ModelFailureKind? KindOfExitCode(int exitCode) => unchecked((uint)exitCode) switch
    {
        0xC0000005 => ModelFailureKind.Crash,        // access violation
        0xC0000409 => ModelFailureKind.Crash,        // stack buffer overrun / fail-fast (abort in a native lib)
        0xC000001D => ModelFailureKind.Crash,        // illegal instruction (CPU without AVX for this build)
        0xC0000017 => ModelFailureKind.OutOfMemory,  // no memory
        0xC000012D => ModelFailureKind.OutOfMemory,  // commitment limit (page file full)
        _ => null,
    };

    /// <summary>The kind, from the error text (the exception chain and the backend's last stderr lines) and the exit code.
    /// Memory wins over crash: a CUDA OOM usually ends in an abort, and the fix is the memory, not the crash.</summary>
    public static ModelFailureKind Classify(string? text, int? exitCode = null)
    {
        text ??= "";
        foreach (var (kind, pattern) in Signs)
            if (pattern.IsMatch(text)) return kind;
        if (exitCode is { } code && KindOfExitCode(code) is { } byCode) return byCode;
        return ModelFailureKind.Unknown;
    }

    /// <summary>The error text worth keeping: every message in the exception chain plus the backend lines that name a reason.</summary>
    public static string Describe(Exception ex, IEnumerable<string>? backendLines = null)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message.Trim());
        if (backendLines != null)
        {
            var telling = backendLines.Where(l => Signs.Any(s => s.Pattern.IsMatch(l))).Select(l => l.Trim()).TakeLast(3).ToList();
            parts.AddRange(telling.Where(l => !parts.Contains(l)));
        }
        return string.Join(" | ", parts);
    }

    // ─── telling the owner ───

    public static string ReasonText(ModelFailureKind kind, bool isThai) => (kind, isThai) switch
    {
        (ModelFailureKind.OutOfMemory, true) => "หน่วยความจำไม่พอ (VRAM/RAM เต็ม)",
        (ModelFailureKind.OutOfMemory, false) => "it ran out of memory (VRAM/RAM full)",
        (ModelFailureKind.Crash, true) => "ตัวรันโมเดล (llama.cpp) ล่ม",
        (ModelFailureKind.Crash, false) => "the model runner (llama.cpp) crashed",
        (ModelFailureKind.Corrupt, true) => "ไฟล์โมเดลอ่านไม่ได้ (อาจดาวน์โหลดไม่ครบหรือเสีย)",
        (ModelFailureKind.Corrupt, false) => "the model file cannot be read (incomplete or damaged download)",
        (ModelFailureKind.Unsupported, true) => "llama.cpp ในเครื่องยังไม่รู้จักสถาปัตยกรรมของโมเดลนี้",
        (ModelFailureKind.Unsupported, false) => "the llama.cpp here does not know this model's architecture",
        (ModelFailureKind.Timeout, true) => "โหลดนานเกินจนหมดเวลา",
        (ModelFailureKind.Timeout, false) => "loading took too long and timed out",
        (_, true) => "เกิดข้อผิดพลาดที่ระบุสาเหตุไม่ได้",
        (_, false) => "an error with no recognisable cause",
    };

    public static string AdviceText(ModelFailureKind kind, bool isThai) => (kind, isThai) switch
    {
        (ModelFailureKind.OutOfMemory, true) => "ลด GPU layers หรือ Context Size, ปิดโปรแกรมที่ใช้การ์ดจอ, หรือใช้ quant ที่เล็กลง (เช่น Q4 แทน Q6) / โมเดลที่เล็กกว่า",
        (ModelFailureKind.OutOfMemory, false) => "lower GPU layers or Context Size, close apps using the GPU, or use a smaller quant (Q4 instead of Q6) / a smaller model",
        (ModelFailureKind.Crash, true) => "ลองอัปเดต llama.cpp ในหน้า Models, ลด GPU layers, หรือใช้โมเดลนี้ผ่าน Ollama",
        (ModelFailureKind.Crash, false) => "update llama.cpp on the Models page, lower GPU layers, or run this model through Ollama",
        (ModelFailureKind.Corrupt, true) => "ลบแล้วดาวน์โหลดไฟล์ใหม่",
        (ModelFailureKind.Corrupt, false) => "delete it and download the file again",
        (ModelFailureKind.Unsupported, true) => "ดาวน์โหลด llama.cpp รุ่นใหม่ในหน้า Models หรือใช้ผ่าน Ollama",
        (ModelFailureKind.Unsupported, false) => "get a newer llama.cpp on the Models page, or use it through Ollama",
        (ModelFailureKind.Timeout, true) => "ลด Context Size / GPU layers หรือรอให้เครื่องว่างแล้วลองใหม่",
        (ModelFailureKind.Timeout, false) => "lower Context Size / GPU layers, or try again when the PC is less busy",
        (_, true) => "ดูรายละเอียดใน log แล้วลองใหม่หลังปรับตั้งค่า",
        (_, false) => "see the details in the log and try again after changing the settings",
    };

    /// <summary>Said when a load just failed (or the server died mid-answer).</summary>
    public static string FailedMessage(ModelFailure f, bool isThai)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(f.Path);
        string detail = f.Detail.Length > 0 ? (isThai ? $"\nรายละเอียด: {f.Detail}" : $"\nDetails: {f.Detail}") : "";
        return isThai
            ? $"⛔ {(f.DuringRun ? "โมเดลหยุดทำงานกลางคัน" : "โหลดโมเดลไม่สำเร็จ")}: {name} — {ReasonText(f.Kind, true)}\n"
              + $"แก้ได้โดย: {AdviceText(f.Kind, true)}\n"
              + "CluadeX จะไม่โหลดโมเดลนี้เองอีก จนกว่าคุณจะกด \"Force load\" ในหน้า Models" + detail
            : $"⛔ {(f.DuringRun ? "The model stopped mid-answer" : "Could not load the model")}: {name} — {ReasonText(f.Kind, false)}\n"
              + $"Way out: {AdviceText(f.Kind, false)}\n"
              + "CluadeX will not load this model by itself again until you press \"Force load\" on the Models page." + detail;
    }

    /// <summary>Said when a load is refused because of an earlier failure.</summary>
    public static string BlockedMessage(ModelFailure f, string currentGpu, bool isThai, out string shortMessage)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(f.Path);
        string when = f.AtUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(isThai ? "th-TH" : "en-US"));
        bool gpuChanged = f.Gpu.Length > 0 && currentGpu.Length > 0 && !string.Equals(f.Gpu, currentGpu, StringComparison.OrdinalIgnoreCase);
        string times = f.Count > 1 ? (isThai ? $" ({f.Count} ครั้ง)" : $" ({f.Count} times)") : "";
        shortMessage = isThai ? $"ไม่โหลด {name}: เคยล้มเหลว — {ReasonText(f.Kind, true)}" : $"Not loading {name}: it failed before — {ReasonText(f.Kind, false)}";
        if (isThai)
            return $"⛔ ไม่โหลด {name} — โมเดลนี้เคย{(f.DuringRun ? "หยุดทำงานกลางคัน" : "โหลดไม่สำเร็จ")}บนเครื่องนี้เมื่อ {when}{times}: {ReasonText(f.Kind, true)}\n"
                + $"แก้ได้โดย: {AdviceText(f.Kind, true)}\n"
                + (gpuChanged ? $"การ์ดจอเปลี่ยนไปจากตอนนั้น ({f.Gpu} → {currentGpu}) อาจโหลดได้แล้ว — " : "")
                + "ถ้าปรับตั้งค่าหรือเปลี่ยนการ์ดจอแล้ว กด \"Force load\" ในหน้า Models เพื่อลองอีกครั้ง";
        return $"⛔ Not loading {name} — it {(f.DuringRun ? "stopped mid-answer" : "failed to load")} on this PC on {when}{times}: {ReasonText(f.Kind, false)}\n"
            + $"Way out: {AdviceText(f.Kind, false)}\n"
            + (gpuChanged ? $"The graphics card has changed since then ({f.Gpu} → {currentGpu}), so it may load now — " : "")
            + "after changing the settings or the card, press \"Force load\" on the Models page to try again.";
    }

    // ─── storage ───

    private Dictionary<string, ModelFailure> Load()
    {
        if (_entries != null) return _entries;
        _entries = new Dictionary<string, ModelFailure>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_file))
            {
                var list = JsonSerializer.Deserialize<List<ModelFailure>>(File.ReadAllText(_file));
                if (list != null)
                    foreach (var f in list.Where(f => !string.IsNullOrEmpty(f.Path)))
                        _entries[Key(f.Path)] = f;
            }
        }
        catch { /* a damaged file must not block every model: start empty */ }
        return _entries;
    }

    private void Save(Dictionary<string, ModelFailure> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* best effort: the in-memory record still blocks for this session */ }
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

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>"NVIDIA GeForce GTX 1070 Ti 8 GB" (+ more cards joined), from GpuDetectionService; empty when unknown.</summary>
    public static string GpuFingerprint(IEnumerable<global::CluadeX.Models.GpuInfo>? gpus)
    {
        if (gpus == null) return "";
        return string.Join(" + ", gpus.Select(g => $"{g.Name} {Math.Round(g.VramTotalBytes / 1073741824.0)} GB"));
    }
}
