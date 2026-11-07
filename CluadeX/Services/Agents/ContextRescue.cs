using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Agents;

/// <summary>
/// What the native loop does when a local model's window overflows, and what it tells the owner when it cannot help.
///
/// Seen 2026-11-06 (Qwen3.5-4B, ctx 16384): the model wrote a 15 KB index.html (the whole file sat in the tool_use
/// input) and then read it back (the whole file again in the tool_result). The overflow "compaction" kept the last
/// 10 / 6 / 3 messages — which were exactly those two blocks — so all three retries failed within 50 ms, the model
/// then ended with no text, and the chat said "finished 2 steps". Here the big blocks are cut first (they are on disk
/// anyway), and a turn that still cannot fit, or that ends silent after a compaction, says so plainly.
/// </summary>
public static class ContextRescue
{
    /// <summary>A tool_result or a tool_use string argument longer than this is cut on the first overflow.</summary>
    public const int FirstCut = 4000;
    /// <summary>…and on a later one, harder.</summary>
    public const int SecondCut = 1200;

    /// <summary>Cuts every tool_result text and tool_use string argument longer than <paramref name="maxChars"/>
    /// to its head and tail with a note saying how to get the rest. Returns the number of characters removed
    /// (0 = nothing was big enough to cut). The newest user message's text is never touched.</summary>
    public static int ShrinkLargeBlocks(List<NativeMessage> messages, int maxChars)
    {
        if (maxChars < 200) maxChars = 200;
        int freed = 0;
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
            {
                if (block.Type == "tool_result" && block.Content is { } text && text.Length > maxChars)
                {
                    string cut = Cut(text, maxChars,
                        "[… {0} characters cut so the conversation fits the model's context window. Read a smaller part " +
                        "of the file again (read_file with offset/limit, or grep) if you need it …]");
                    freed += text.Length - cut.Length;
                    block.Content = cut;
                }
                else if (block.Type == "tool_use" && block.Input is { ValueKind: JsonValueKind.Object } input)
                {
                    var (shrunk, gone) = ShrinkStrings(input, maxChars);
                    if (gone > 0) { block.Input = shrunk; freed += gone; }
                }
            }
        }
        return freed;
    }

    private static (JsonElement Input, int Freed) ShrinkStrings(JsonElement input, int maxChars)
    {
        bool any = false;
        foreach (var p in input.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString()?.Length ?? 0) > maxChars) { any = true; break; }
        if (!any) return (input, 0);

        int freed = 0;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in input.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s && s.Length > maxChars)
                {
                    string cut = Cut(s, maxChars,
                        "[… {0} characters already sent with this call (they are in the file now) — not repeated here …]");
                    freed += s.Length - cut.Length;
                    w.WriteString(p.Name, cut);
                }
                else p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return (doc.RootElement.Clone(), freed);
    }

    private static string Cut(string text, int maxChars, string noteFormat)
    {
        int head = maxChars * 2 / 3, tail = maxChars / 6;
        int removed = text.Length - head - tail;
        if (removed <= 0) return text;
        var sb = new StringBuilder(head + tail + 200);
        sb.Append(text, 0, head).Append("\n\n").Append(string.Format(noteFormat, removed)).Append("\n\n");
        sb.Append(text, text.Length - tail, tail);
        return sb.ToString();
    }

    /// <summary>The run stopped because the window stayed full after every rescue.</summary>
    public static string OverflowStopMessage(int ctxTokens, bool isLocal, int steps, bool isThai)
    {
        if (isThai)
            return $"⛔ **หยุดงาน: context ของโมเดลเต็ม** (ขนาด {ctxTokens:N0} token) หลังทำไป {steps} ขั้น — ตัดข้อความยาวและย่อประวัติแล้วก็ยังไม่พอ งานจึงยังไม่เสร็จ\n\n"
                + "**แก้ได้โดย**\n"
                + (isLocal
                    ? "- เพิ่ม **Context Size** ใน Settings ▸ AI Provider (ถ้า VRAM พอ) แล้วโหลดโมเดลใหม่\n- หรือใช้โมเดลที่รับ context ยาวกว่า / โมเดล API\n"
                    : "- เปลี่ยนเป็นโมเดลที่รับ context ยาวกว่า\n")
                + "- หรือแบ่งงานให้เล็กลง (เช่น ทีละไฟล์) แล้วพิมพ์ \"ทำต่อ\"";
        return $"⛔ **Stopped: the model's context window is full** ({ctxTokens:N0} tokens) after {steps} step(s) — cutting long blocks and compacting the history was not enough, so the task is not finished.\n\n"
            + "**Ways out**\n"
            + (isLocal
                ? "- Raise **Context Size** in Settings ▸ AI Provider (if VRAM allows) and reload the model\n- Or use a model with a longer window / an API model\n"
                : "- Switch to a model with a longer window\n")
            + "- Or split the task smaller (one file at a time) and say \"continue\"";
    }

    /// <summary>A local model gave no answer within the time budget. A retry would take as long again: the GPU/CPU is the
    /// limit (a model bigger than the VRAM runs partly on the CPU, and a long prompt is processed before the first word).</summary>
    public static string LocalTooSlowMessage(string? model, int waitedSec, int promptTokens, int steps, bool isThai)
    {
        string name = string.IsNullOrWhiteSpace(model) ? (isThai ? "โมเดล local" : "the local model") : Path.GetFileNameWithoutExtension(model);
        string waited = waitedSec >= 120 ? (isThai ? $"{waitedSec / 60} นาที" : $"{waitedSec / 60} min") : (isThai ? $"{waitedSec} วินาที" : $"{waitedSec} s");
        if (isThai)
            return $"⏱️ **หยุดงาน: {name} ตอบไม่ทัน** — รอ {waited} แล้วยังไม่ได้คำตอบเลย (ขั้นที่ {steps}, prompt ≈ {promptTokens:N0} token) งานจึงยังไม่ได้ทำ\n\n"
                + "โมเดลที่ใหญ่กว่า VRAM จะรันบางส่วนบน CPU ซึ่งช้ามาก และต้องอ่าน prompt ทั้งหมดก่อนตอบคำแรก — ลองใหม่ก็จะช้าเท่าเดิม\n\n"
                + "**แก้ได้โดย**\n"
                + "- ใช้โมเดลที่เล็กพอให้อยู่ใน VRAM ทั้งหมด (ดูป้าย Fit ในหน้า Models)\n"
                + "- ลด **Context Size** / ปิด MCP ที่ไม่ใช้ เพื่อให้ prompt สั้นลง\n"
                + "- หรือเพิ่ม **หมดเวลาเมื่อไม่มีการตอบ** ใน Settings (0 = รอได้ไม่จำกัด) ถ้ายอมรอ";
        return $"⏱️ **Stopped: {name} did not answer in time** — no answer after {waited} (step {steps}, prompt ≈ {promptTokens:N0} tokens), so the task was not done.\n\n"
            + "A model bigger than the VRAM runs partly on the CPU, which is very slow, and it reads the whole prompt before its first word — a retry would be just as slow.\n\n"
            + "**Ways out**\n"
            + "- Use a model small enough to sit fully in VRAM (see the Fit badge on the Models page)\n"
            + "- Lower **Context Size** / turn off unused MCP servers so the prompt is shorter\n"
            + "- Or raise **Idle timeout** in Settings (0 = wait forever) if you are willing to wait";
    }

    /// <summary>The model ended silently after the history had to be compacted: not "done" — it lost its place.</summary>
    public static string SilentAfterCompactMessage(int ctxTokens, int compactions, int steps, bool isThai)
    {
        if (isThai)
            return $"⚠️ **งานอาจยังไม่เสร็จ** — ระหว่างทำ context ({ctxTokens:N0} token) เต็มจนต้องตัด/ย่อประวัติ {compactions} ครั้ง แล้วโมเดลหยุดโดยไม่ได้สรุปอะไร (ทำไป {steps} ขั้น) "
                + "ตรวจไฟล์ที่ได้ก่อนใช้ แล้วพิมพ์ \"ทำต่อ\" หรือเพิ่ม Context Size / ใช้โมเดลที่ใหญ่กว่า";
        return $"⚠️ **The task may not be finished** — the context window ({ctxTokens:N0} tokens) filled up and the history was cut/compacted {compactions} time(s), then the model stopped without a summary ({steps} step(s)). "
            + "Check the files before using them, then say \"continue\", or raise Context Size / use a bigger model.";
    }
}
