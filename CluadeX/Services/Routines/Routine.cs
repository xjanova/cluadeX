using System.Globalization;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Routines;

/// <summary>
/// One scheduled task (owner 2026-11-11, after comparing CluadeX with Hermes Agent: "งานตั้งเวลาอัตโนมัติ"): at its time
/// CluadeX opens a chat in <see cref="Project"/> — or carries on in the same one — and sends <see cref="Prompt"/>, as if
/// the owner had typed it. The turn runs under the owner's ordinary rules: a card that asks waits for them.
/// Kept in routines.json beside codex.db (<see cref="RoutineService"/>).
/// </summary>
public sealed class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>The project folder the chat works in ("" = no project).</summary>
    public string Project { get; set; } = "";
    /// <summary>What the chat is told each time.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>daily | interval | once</summary>
    public string Kind { get; set; } = RoutineSchedule.Daily;
    /// <summary>daily: the time of day, "HH:mm".</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>daily: the days it runs, bit 0 = Sunday … bit 6 = Saturday (127 = every day).</summary>
    public int Days { get; set; } = RoutineSchedule.EveryDay;
    /// <summary>interval: minutes between runs (at least <see cref="RoutineSchedule.MinInterval"/>).</summary>
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>once: when (local time).</summary>
    public DateTime? OnceAt { get; set; }

    /// <summary>A run missed while CluadeX was closed (or the PC asleep) runs once when it is back; false = skip it.</summary>
    public bool CatchUp { get; set; } = true;
    /// <summary>true = every run carries on in the same chat (it sees the earlier runs); false = a new chat each time.</summary>
    public bool SameChat { get; set; }
    /// <summary>The chat the last run used (the one <see cref="SameChat"/> carries on in).</summary>
    public string? ChatId { get; set; }

    /// <summary>"you" (made on the page) | "ai" (made by a chat's schedule_task, after the owner allowed it).</summary>
    public string CreatedBy { get; set; } = "you";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    /// <summary>"" | running | ran | failed | missed (skipped, CatchUp off). <see cref="LastNote"/> "busy": a time went by
    /// while the last run still worked (skipped); "missed-run": this run makes up for a missed time.</summary>
    public string LastStatus { get; set; } = "";
    public string LastNote { get; set; } = "";

    public Routine Copy() => (Routine)MemberwiseClone();
}

/// <summary>When a routine runs next, how that reads, and the short form a chat's schedule_task passes. Pure.</summary>
public static class RoutineSchedule
{
    public const string Daily = "daily", Interval = "interval", Once = "once";
    public const int EveryDay = 0b111_1111, Weekdays = 0b011_1110, Weekend = 0b100_0001;
    public const int MinInterval = 15;
    /// <summary>A routine made by a chat runs at most this often: a model that read untrusted text cannot set a loop going.</summary>
    public const int MinIntervalForAi = 60;

    /// <summary>The time of day, or null when <paramref name="hhmm"/> is not "H:mm" / "HH:mm".</summary>
    public static TimeSpan? TimeOf(string? hhmm)
    {
        var m = Regex.Match((hhmm ?? "").Trim(), @"^(\d{1,2})[:.](\d{2})$");
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
        return h < 24 && min < 60 ? new TimeSpan(h, min, 0) : null;
    }

    /// <summary>Why <paramref name="r"/> cannot be scheduled, or null when it can.</summary>
    public static string? Problem(Routine r, bool byAi = false)
    {
        if (string.IsNullOrWhiteSpace(r.Prompt)) return "routine.err.prompt";
        switch (r.Kind)
        {
            case Daily:
                if (TimeOf(r.Time) == null) return "routine.err.time";
                if ((r.Days & EveryDay) == 0) return "routine.err.days";
                return null;
            case Interval:
                return r.IntervalMinutes < (byAi ? MinIntervalForAi : MinInterval) ? (byAi ? "routine.err.intervalAi" : "routine.err.interval") : null;
            case Once:
                return r.OnceAt == null ? "routine.err.once" : null;
            default:
                return "routine.err.kind";
        }
    }

    /// <summary>The first time after <paramref name="after"/> that <paramref name="r"/> runs; null = never again.</summary>
    public static DateTime? Next(Routine r, DateTime after)
    {
        switch (r.Kind)
        {
            case Daily:
            {
                if (TimeOf(r.Time) is not { } t || (r.Days & EveryDay) == 0) return null;
                for (int d = 0; d <= 7; d++)
                {
                    var at = after.Date.AddDays(d) + t;
                    if (at > after && (r.Days & (1 << (int)at.DayOfWeek)) != 0) return at;
                }
                return null;
            }
            case Interval:
            {
                if (r.IntervalMinutes < MinInterval) return null;
                var step = TimeSpan.FromMinutes(r.IntervalMinutes);
                var anchor = r.LastRunAt ?? r.CreatedAt;
                if (anchor > after) return anchor;
                long k = (after - anchor).Ticks / step.Ticks + 1;
                return anchor + TimeSpan.FromTicks(step.Ticks * k);
            }
            case Once:
                return r.OnceAt is { } once && once > after && r.LastRunAt == null ? once : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The short form a chat passes to schedule_task: "daily 02:00", "weekdays 09:00", "weekend 10:00",
    /// "mon,wed,fri 08:30", "every 2h", "every 30m", "once 2026-11-12 14:00". Fills <paramref name="r"/>'s schedule;
    /// returns why it cannot be read, or null.
    /// </summary>
    public static string? Parse(string? text, Routine r)
    {
        string s = Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
        if (s.Length == 0) return "routine.err.schedule";
        var every = Regex.Match(s, @"^every (\d+) ?(m|min|mins|minutes?|h|hr|hrs|hours?)$");
        if (every.Success)
        {
            int n = int.Parse(every.Groups[1].Value);
            r.Kind = Interval;
            r.IntervalMinutes = every.Groups[2].Value.StartsWith('h') ? n * 60 : n;
            return null;
        }
        var once = Regex.Match(s, @"^once (\d{4}-\d{2}-\d{2})[ t](\d{1,2}[:.]\d{2})$");
        if (once.Success)
        {
            if (!DateTime.TryParseExact(once.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || TimeOf(once.Groups[2].Value) is not { } at)
                return "routine.err.schedule";
            r.Kind = Once;
            r.OnceAt = day + at;
            return null;
        }
        var daily = Regex.Match(s, @"^(daily|every day|weekdays|weekends?|[a-z,]+) (\d{1,2}[:.]\d{2})$");
        if (!daily.Success || TimeOf(daily.Groups[2].Value) is not { } time) return "routine.err.schedule";
        int days = daily.Groups[1].Value switch
        {
            "daily" or "every day" => EveryDay,
            "weekdays" => Weekdays,
            "weekend" or "weekends" => Weekend,
            var list => DaysOf(list),
        };
        if (days == 0) return "routine.err.schedule";
        r.Kind = Daily;
        r.Days = days;
        r.Time = $"{time.Hours:00}:{time.Minutes:00}";
        return null;
    }

    private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    private static int DaysOf(string list)
    {
        int mask = 0;
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = Array.FindIndex(DayNames, d => part.StartsWith(d, StringComparison.Ordinal));
            if (i < 0) return 0;
            mask |= 1 << i;
        }
        return mask;
    }

    /// <summary>Dates on the page in the UI language (the machine may be Thai while the UI is English); text for a
    /// model uses <see cref="CultureInfo.InvariantCulture"/> — a Thai culture writes the Buddhist year (2569).</summary>
    public static CultureInfo Culture(string? language) =>
        CultureInfo.GetCultureInfo(language == "th" ? "th-TH" : "en-US");

    /// <summary>How the schedule reads ("every day at 02:00"), with <paramref name="l"/> the localized strings and
    /// <paramref name="culture"/> for a date (default invariant).</summary>
    public static string Describe(Routine r, Func<string, string> l, CultureInfo? culture = null)
    {
        switch (r.Kind)
        {
            case Daily:
                string days = r.Days == EveryDay ? l("routine.days.every")
                    : r.Days == Weekdays ? l("routine.days.weekdays")
                    : r.Days == Weekend ? l("routine.days.weekend")
                    : string.Join(" ", Enumerable.Range(0, 7).Where(i => (r.Days & (1 << i)) != 0).Select(i => l("routine.day." + i)));
                return string.Format(l("routine.describe.daily"), days, r.Time);
            case Interval:
                return r.IntervalMinutes % 60 == 0
                    ? string.Format(l("routine.describe.hours"), r.IntervalMinutes / 60)
                    : string.Format(l("routine.describe.minutes"), r.IntervalMinutes);
            case Once:
                return string.Format(l("routine.describe.once"), r.OnceAt?.ToString("d MMM HH:mm", culture ?? CultureInfo.InvariantCulture) ?? "?");
            default:
                return "?";
        }
    }
}
