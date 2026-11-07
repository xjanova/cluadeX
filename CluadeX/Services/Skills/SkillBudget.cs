using CluadeX.Helpers;
using CluadeX.Models;

namespace CluadeX.Services.Skills;

/// <summary>
/// What switched-on skills cost (owner 2026-11-07: "การเปิดสกิลเยอะจะทำให้โทเค็นตอนโหลดแพงหรือเต็มมากขึ้น ดังนั้นควรมีเกจบอก
/// ด้วยว่าขีดจำกัดอยู่ตรงไหน"). Two prices per skill:
///   listing — the one line that names it to the model, paid on EVERY turn while it is offered;
///   load    — its whole prompt, paid once, only when the model (or /command) actually runs it.
/// The line is written here and nowhere else, so the gauge measures exactly the text the prompt carries.
/// Pure: the Skills page, the chat's picker and the harness all read the same numbers.
/// </summary>
public static class SkillBudget
{
    /// <summary>The heading and the "call skill_invoke …" sentence above the lines.</summary>
    public const int HeaderTokens = 60;

    /// <summary>One skill's line in the model's list (the system prompt's, or a turn's picked list).</summary>
    public static string ListingLine(SkillDefinition s, bool isThai) =>
        string.IsNullOrWhiteSpace(s.WhenToUse)
            ? $"- `{s.Name}`: {s.Description}"
            : $"- `{s.Name}`: {s.Description} ({(isThai ? "ใช้เมื่อ" : "use when")}: {s.WhenToUse})";

    /// <summary>Tokens the line costs on every turn it is offered (the newline included).</summary>
    public static int ListingTokens(SkillDefinition s, bool isThai) => TokenBudget.EstimateTokens(ListingLine(s, isThai)) + 1;

    /// <summary>Tokens its prompt costs on the turn it runs (skill_invoke's heading included).</summary>
    public static int LoadTokens(SkillDefinition s) =>
        TokenBudget.EstimateTokens(s.PromptContent) + TokenBudget.EstimateTokens(s.Description) + 30;

    /// <summary>The recommended ceiling for the list: 1 % of the model's window, never under 250 tokens (a small local
    /// window still names a few skills) and never over 4,000 (more lines than that is noise, whatever the window).</summary>
    public static int CapFor(int contextWindow) => Math.Clamp(Math.Max(0, contextWindow) / 100, 250, 4000);

    /// <summary>A skill whose prompt alone takes more than a tenth of the window crowds the task out when it runs.</summary>
    public static bool HeavyToLoad(SkillDefinition s, int contextWindow) =>
        contextWindow > 0 && LoadTokens(s) > contextWindow / 10;

    public enum Level { Empty, Light, Near, Over }

    /// <param name="Used">tokens the list costs per turn</param>
    /// <param name="Cap">the recommended ceiling (<see cref="CapFor"/>)</param>
    /// <param name="Fill">the bar's fill, 0..1 — the ceiling sits at 0.5, so "twice the ceiling" fills the bar</param>
    public sealed record Gauge(int Used, int Cap, int ContextWindow, int Count, Level Level, double Fill)
    {
        /// <summary>Share of the whole window the list takes, in percent.</summary>
        public double PercentOfWindow => ContextWindow > 0 ? Used * 100.0 / ContextWindow : 0;
    }

    /// <summary>Measure a list of offered skills against <paramref name="contextWindow"/>.</summary>
    public static Gauge Measure(IEnumerable<SkillDefinition> offered, bool isThai, int contextWindow)
    {
        var list = offered.ToList();
        int used = list.Count == 0 ? 0 : HeaderTokens + list.Sum(s => ListingTokens(s, isThai));
        int cap = CapFor(contextWindow);
        var level = list.Count == 0 ? Level.Empty
            : used <= cap * 8 / 10 ? Level.Light
            : used <= cap ? Level.Near
            : Level.Over;
        return new Gauge(used, cap, contextWindow, list.Count, level, Math.Clamp(used / (2.0 * cap), 0, 1));
    }
}
