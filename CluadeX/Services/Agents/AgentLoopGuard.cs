using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>What the loop guard wants done after a step.</summary>
public enum LoopGuardAction { None, Nudge, Stop }

/// <summary>The guard's verdict on one step: what to do, the call that repeats, how many times, and its error.</summary>
public sealed record LoopGuardVerdict(LoopGuardAction Action, string Kind, string Tool, string Args, int Repeats, string Error)
{
    public static readonly LoopGuardVerdict None = new(LoopGuardAction.None, "", "", "", 0, "");
}

/// <summary>
/// Notices an agent run going round in circles and says when to nudge the model and when to stop the run — for every
/// provider, with or without escalation.
/// <para>Why it exists (2026-11-04, "สร้าง เกม sudoku แบบเล่นผ่านเว็บ" on Ollama llama3.2:3b): the model read a file that
/// did not exist, got "File not found", and read it again — nine times, then kept going, one step every ~35 s. The only
/// repeated-error check sat inside the escalation block, so with escalation off (the default) and no step limit (0 =
/// unlimited) nothing could ever end the run, and the owner was never told why it failed.</para>
/// <para>What counts: the same call (tool + arguments) ending the same way. A failing call is cheap to spot — the second
/// identical failure is a nudge, the fourth a stop. A succeeding call repeated with the same arguments AND the same
/// result, with no file changed in between (the same search, the same read), is a nudge at three and a stop at six —
/// a screenshot or a log poll whose result keeps changing never counts. Every step where every call fails counts
/// toward a streak that stops the run at six. A successful file change is progress: it clears everything.</para>
/// </summary>
public sealed class AgentLoopGuard
{
    public const int FailNudgeAt = 2, FailStopAt = 4;
    public const int SameNudgeAt = 3, SameStopAt = 6;
    public const int FailedStepsStopAt = 6;

    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _repeats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nudged = new(StringComparer.Ordinal);
    private int _failedStepsInARow;

    /// <summary>One tool call of a step: the name, its arguments as sent, whether it worked, its error text, whether it
    /// changed a file, and (for a success) what it returned — a screenshot or a log poll repeated with the same arguments
    /// is not a loop while what comes back keeps changing.</summary>
    public readonly record struct Call(string Tool, string Args, bool Success, string Error, bool Mutated = false, string Output = "");

    /// <summary>Steps in a row where every call failed.</summary>
    public int FailedStepsInARow => _failedStepsInARow;

    /// <summary>Forget everything (after an escalation: the new model gets a clean slate).</summary>
    public void Reset()
    {
        _failures.Clear();
        _repeats.Clear();
        _nudged.Clear();
        _failedStepsInARow = 0;
    }

    /// <summary>Feed one finished step; get back what to do. A step with no tool calls (prose) returns None.</summary>
    public LoopGuardVerdict Observe(IReadOnlyList<Call> calls)
    {
        if (calls.Count == 0) return LoopGuardVerdict.None;

        // A real change is progress: whatever repeated before it may now give a different answer.
        if (calls.Any(c => c.Success && c.Mutated))
        {
            Reset();
            return LoopGuardVerdict.None;
        }

        _failedStepsInARow = calls.All(c => !c.Success) ? _failedStepsInARow + 1 : 0;

        LoopGuardVerdict best = LoopGuardVerdict.None;
        foreach (var c in calls)
        {
            string args = NormalizeArgs(c.Args);
            string err = NormalizeError(c.Error);
            LoopGuardVerdict v;
            if (!c.Success)
            {
                string key = c.Tool + "\u0001" + args + "\u0001" + err;
                int n = _failures[key] = _failures.GetValueOrDefault(key) + 1;
                v = Judge(key, n, FailNudgeAt, FailStopAt, "same-error", c, args);
            }
            else
            {
                string key = c.Tool + "\u0001" + args + "\u0001" + OutputDigest(c.Output);
                int n = _repeats[key] = _repeats.GetValueOrDefault(key) + 1;
                v = Judge(key, n, SameNudgeAt, SameStopAt, "same-call", c, args);
            }
            if (v.Action > best.Action) best = v;
        }

        if (best.Action != LoopGuardAction.Stop && _failedStepsInARow >= FailedStepsStopAt)
        {
            var last = calls[^1];
            best = new LoopGuardVerdict(LoopGuardAction.Stop, "all-failing", last.Tool, NormalizeArgs(last.Args),
                _failedStepsInARow, Short(last.Error));
        }
        return best;
    }

    private LoopGuardVerdict Judge(string key, int n, int nudgeAt, int stopAt, string kind, Call c, string args)
    {
        if (n >= stopAt)
            return new LoopGuardVerdict(LoopGuardAction.Stop, kind, c.Tool, args, n, Short(c.Error));
        // One nudge per call: telling a model the same thing every step only fills its window.
        if (n >= nudgeAt && _nudged.Add(key))
            return new LoopGuardVerdict(LoopGuardAction.Nudge, kind, c.Tool, args, n, Short(c.Error));
        return LoopGuardVerdict.None;
    }

    /// <summary>Arguments in a stable form: JSON objects with sorted keys and trimmed string values, so the same call
    /// written with keys in another order or a trailing space still counts as the same call.</summary>
    public static string NormalizeArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(args);
            return Canon(doc.RootElement);
        }
        catch (JsonException)
        {
            return args.Trim();
        }
    }

    private static string Canon(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canon(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize((e.GetString() ?? "").Trim()),
        _ => e.GetRawText(),
    };

    private static readonly Regex Volatile = new(@"\d+(\.\d+)?\s*(ms|s|sec|seconds)\b|\b\d{1,2}:\d{2}(:\d{2})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>An error without the parts that change on every run (timings, clock times), cut to its head.</summary>
    public static string NormalizeError(string? error)
    {
        string s = Volatile.Replace((error ?? "").Replace("\r", "").Trim(), "#");
        return s.Length > 300 ? s[..300] : s;
    }

    /// <summary>A short fingerprint of a result (timings and clock times taken out), so results are compared without
    /// keeping a whole file's text per call.</summary>
    public static string OutputDigest(string? output)
    {
        string s = Volatile.Replace((output ?? "").Replace("\r", ""), "#");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash, 0, 8);
    }

    private static string Short(string? s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    /// <summary>The argument worth showing a person: the path, query or command, else the arguments cut short.</summary>
    public static string ShowArgs(string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "path", "file_path", "query", "command", "url", "pattern", "name" })
                    if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                        return Cut(v.GetString()!.Trim(), 80);
        }
        catch (JsonException) { }
        return args is "{}" or "" ? "" : Cut(args, 80);
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ─── What the model is told ───

    /// <summary>The nudge for the model: what repeated, why repeating cannot help, and what to do instead.</summary>
    public static string NudgeText(LoopGuardVerdict v)
    {
        string what = $"{v.Tool}({ShowArgs(v.Args)})";
        if (v.Kind == "same-error")
        {
            string fix = LooksNotFound(v.Error)
                ? "The file does not exist. Do NOT read or edit it again — create it with write_file (path + the COMPLETE content)."
                : "Change the arguments, use a different tool, or — if this cannot be done — say so plainly in your final answer.";
            return $"[loop] You called {what} {v.Repeats} times and it failed the same way every time: \"{v.Error}\". "
                 + $"Calling it again will give the same error. {fix} "
                 + $"If it fails {FailStopAt} times the run will be stopped.";
        }
        return $"[loop] You called {what} {v.Repeats} times with the same arguments and nothing changed in between, "
             + "so the result is the same as before — it is already above. Use it and move to the next step, or give your "
             + $"final answer. At {SameStopAt} repeats the run will be stopped.";
    }

    public static bool LooksNotFound(string? error)
    {
        string e = error ?? "";
        return e.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || e.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || e.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            || e.Contains("FileNotFound", StringComparison.Ordinal);
    }

    // ─── What the user is told ───

    /// <summary>The answer the user sees when the run is stopped: what happened, why it is the model, and what to do.</summary>
    /// <param name="modelLabel">e.g. "Ollama · llama3.2:3b".</param>
    /// <param name="isLocal">a model on this machine (the advice differs: a bigger model or escalation).</param>
    /// <param name="capability">the capability estimate for the model, or null when it is not known.</param>
    /// <param name="escalated">the run was already handed to a stronger model, which got stuck too.</param>
    public static string StopMessage(LoopGuardVerdict v, string modelLabel, bool isLocal, ModelCapability.Estimate? capability,
        bool escalated, int steps, bool isThai)
    {
        string arg = ShowArgs(v.Args);
        string call = arg.Length > 0 ? $"`{v.Tool}` ({arg})" : $"`{v.Tool}`";
        string err = v.Error.Length > 0 ? v.Error : (isThai ? "(ไม่มีข้อความ)" : "(no message)");
        var sb = new System.Text.StringBuilder();
        if (isThai)
        {
            sb.Append($"⛔ หยุดงานแล้ว — โมเดล **{modelLabel}** ติดวนอยู่ที่เดิม ทำต่อไปก็ไม่คืบหน้า\n\n");
            sb.Append(v.Kind switch
            {
                "same-error" => $"- เรียก {call} ซ้ำ {v.Repeats} ครั้ง ได้ผิดพลาดเดิมทุกครั้ง: {err}\n",
                "same-call" => $"- เรียก {call} ซ้ำ {v.Repeats} ครั้งด้วยค่าเดิม โดยไม่มีอะไรเปลี่ยนระหว่างนั้น\n",
                _ => $"- {v.Repeats} ขั้นติดกันที่ทุกคำสั่งล้มเหลว ล่าสุด {call}: {err}\n",
            });
            sb.Append($"- ทำไปแล้ว {steps} ขั้น งานที่ทำสำเร็จก่อนหน้านี้ยังอยู่ครบ\n");
            if (escalated)
                sb.Append("- ส่งต่อให้โมเดลที่แรงกว่าแล้ว แต่ก็ยังติด — งานนี้อาจต้องให้คุณช่วยบอกรายละเอียดเพิ่ม\n");
            if (capability is { } cap && cap.Tier != ModelCapability.Tier.Capable)
                sb.Append($"- {cap.SummaryTh}\n");
            sb.Append("\n**ทางแก้:**\n");
            if (isLocal)
            {
                sb.Append("1. เปลี่ยนเป็นโมเดลที่ใหญ่กว่านี้ (โมเดลสายโค้ด 7B ขึ้นไป) หรือใช้ Claude / API\n");
                if (!escalated)
                    sb.Append("2. หรือเปิด \"ส่งต่อให้ Claude เมื่อโมเดลในเครื่องติด\" ในการตั้งค่า แล้วพิมพ์ \"ทำต่อ\"\n");
                sb.Append($"{(escalated ? 2 : 3)}. หรือแบ่งงานให้เล็กลง บอกให้ชัดว่าต้องสร้างไฟล์อะไร\n");
            }
            else
            {
                sb.Append("1. บอกรายละเอียดเพิ่ม หรือแบ่งงานให้เล็กลง แล้วพิมพ์ \"ทำต่อ\"\n");
                sb.Append("2. หรือลองเปลี่ยนโมเดล\n");
            }
        }
        else
        {
            sb.Append($"⛔ Stopped — the model **{modelLabel}** is going round in circles; more steps will not get it further.\n\n");
            sb.Append(v.Kind switch
            {
                "same-error" => $"- It called {call} {v.Repeats} times and got the same error each time: {err}\n",
                "same-call" => $"- It called {call} {v.Repeats} times with the same arguments, with nothing changing in between.\n",
                _ => $"- {v.Repeats} steps in a row where every call failed; the last was {call}: {err}\n",
            });
            sb.Append($"- {steps} steps were run; whatever succeeded before this is kept.\n");
            if (escalated)
                sb.Append("- It was already handed to a stronger model, which got stuck too — the task may need more detail from you.\n");
            if (capability is { } cap && cap.Tier != ModelCapability.Tier.Capable)
                sb.Append($"- {cap.SummaryEn}\n");
            sb.Append("\n**What to do:**\n");
            if (isLocal)
            {
                sb.Append("1. Switch to a bigger model (a coding model of 7B or more), or to Claude / an API model.\n");
                if (!escalated)
                    sb.Append("2. Or turn on \"Escalate to Claude when a local model gets stuck\" in Settings, then say \"continue\".\n");
                sb.Append($"{(escalated ? 2 : 3)}. Or make the task smaller and name the files it should create.\n");
            }
            else
            {
                sb.Append("1. Give more detail or a smaller task, then say \"continue\".\n");
                sb.Append("2. Or try another model.\n");
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// What a model can be trusted with, from its size — said to the user BEFORE a run, so a 3B model is not handed a
/// whole game and left to fail (owner 2026-11-04: "ต้องทราบว่าทำได้แค่ไหนก็ต้องแจ้งผู้ใช้").
/// The size comes from the name ("llama3.2:3b", "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf"); an unknown size says nothing.
/// </summary>
public static class ModelCapability
{
    public enum Tier { Unknown, Tiny, Small, Capable }

    public sealed record Estimate(Tier Tier, double? Billions, string SummaryTh, string SummaryEn);

    // "3b", ":3b", "-7B-", "1.5b", "e4b" (gemma 3n), "8x7b" (MoE: experts × size). Not "v3", not "3.2" (a version).
    // Not "a3b" either: a MoE's ACTIVE size ("Qwen3.6-35B-A3B", colibri's "qwen36-35b-a3b") — the model is the 35B.
    private static readonly Regex Size = new(@"(?:(?<experts>\d+)x)?(?<!\d|\.|[aA])e?(?<n>\d{1,3}(?:\.\d+)?)b(?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The model's size in billions of parameters, from its name; null when the name does not say.</summary>
    public static double? ParseBillions(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        // Not Path.GetFileNameWithoutExtension: "llama3.2:3b" has no extension, but it would cut ".2:3b" off as one.
        string name = modelName.Trim();
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        Match? last = null;
        foreach (Match m in Size.Matches(name)) last = m;   // the size tag comes after the family/version
        if (last == null) return null;
        if (!double.TryParse(last.Groups["n"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n) || n <= 0) return null;
        if (int.TryParse(last.Groups["experts"].Value, out int experts) && experts > 1) n *= experts;
        return n;
    }

    /// <summary>The estimate for a model running on this machine. API models are never judged by name.</summary>
    public static Estimate ForLocal(string? modelName)
    {
        double? b = ParseBillions(modelName);
        string size = b is { } v ? v.ToString(v % 1 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + "B" : "";
        return b switch
        {
            null => new Estimate(Tier.Unknown, null, "", ""),
            < 4 => new Estimate(Tier.Tiny, b,
                $"โมเดลนี้เล็กมาก ({size}) — ตอบคำถามและแก้ไฟล์สั้น ๆ ทีละไฟล์ได้ แต่งานหลายขั้น (สร้างเกม/แอพทั้งตัว) มักทำไม่สำเร็จ",
                $"This model is very small ({size}) — fine for questions and short single-file edits, but multi-step work (a whole game or app) usually fails."),
            < 7 => new Estimate(Tier.Small, b,
                $"โมเดลนี้ค่อนข้างเล็ก ({size}) — งานง่าย 1-2 ไฟล์พอได้ งานใหญ่หลายไฟล์อาจติดหรือหลงทาง",
                $"This model is fairly small ({size}) — simple one- or two-file tasks are OK; large multi-file work may stall."),
            _ => new Estimate(Tier.Capable, b, "", ""),
        };
    }

    /// <summary>The line shown before a non-trivial run on a small local model; null when there is nothing to warn about.</summary>
    public static string? PreRunWarning(Estimate e, bool escalationOn, bool isThai)
    {
        if (e.Tier is not (Tier.Tiny or Tier.Small)) return null;
        if (isThai)
            return $"⚠ {e.SummaryTh}" + (escalationOn
                ? " — ถ้าติด จะส่งต่อให้โมเดลที่แรงกว่าเอง"
                : " — ถ้าติดวน จะหยุดแล้วบอกเหตุผล แนะนำใช้โมเดล 7B ขึ้นไปหรือเปิด \"ส่งต่อให้ Claude\" ในการตั้งค่า");
        return $"⚠ {e.SummaryEn}" + (escalationOn
            ? " If it gets stuck, a stronger model takes over."
            : " If it loops it will be stopped with the reason; a 7B+ model or \"Escalate to Claude\" in Settings is recommended.");
    }
}
