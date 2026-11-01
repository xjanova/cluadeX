namespace CluadeX.Services.Localization;

/// <summary>Localized strings for effort-aware dispatch: Settings ▸ Agent ▸ dispatch card and the spend window's
/// per-strategy breakdown. Every key needs both "en" and "th".</summary>
internal static class DispatchKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── Settings ▸ Agent ──
        ["settings.card.dispatch"] = new() { ["en"] = "Sub-agents by effort", ["th"] = "เอเจนต์ย่อยตามระดับความพยายาม" },
        ["settings.dispatchIntro"] = new()
        {
            ["en"] = "Effort decides when the agent splits work: low — none; medium — read-only helpers on the cheapest model; "
                   + "high — workers in their own git worktrees, reviewed before the merge; xhigh/max — also races fixers on a red build. "
                   + "The run's spend budget caps it.",
            ["th"] = "ระดับความพยายามเป็นตัวตัดสินว่าจะแยกงานเมื่อไหร่: low — ไม่แยก · medium — เอเจนต์ย่อยแบบอ่านอย่างเดียวบนโมเดลที่ถูกที่สุด · "
                   + "high — ผู้ทำงานแยกกันใน git worktree ของตัวเอง ตรวจก่อนรวม · xhigh/max — ส่งตัวแก้หลายตัวแข่งกันเมื่อ build แดงด้วย "
                   + "งบต่อรอบจำกัดไว้เสมอ",
        },
        ["settings.dispatchOn"] = new() { ["en"] = "Dispatch by effort", ["th"] = "แยกงานตามระดับความพยายาม" },
        ["settings.dispatchOnDesc"] = new()
        {
            ["en"] = "Off = sub-agents work as before (the model decides, same model, no worktrees).",
            ["th"] = "ปิด = เอเจนต์ย่อยทำงานแบบเดิม (โมเดลตัดสินเอง ใช้โมเดลเดียวกัน ไม่มี worktree)",
        },
        ["settings.maxParallel"] = new() { ["en"] = "Max parallel agents", ["th"] = "จำนวนเอเจนต์ที่ทำพร้อมกันสูงสุด" },
        ["settings.maxParallelDesc"] = new() { ["en"] = "Per run, 1–8.", ["th"] = "ต่อหนึ่งรอบงาน 1–8 ตัว" },
        ["settings.cliRunBudget"] = new() { ["en"] = "CLI agents: budget per run (USD)", ["th"] = "เอเจนต์ CLI: งบต่อรอบ (ดอลลาร์)" },
        ["settings.cliRunBudgetDesc"] = new()
        {
            ["en"] = "Claude Dev stops the turn at this API-equivalent value, sub-agents included. 0 = no limit.",
            ["th"] = "Claude Dev หยุดเทิร์นเมื่อมูลค่าเทียบ API ถึงค่านี้ รวมเอเจนต์ย่อย ใส่ 0 = ไม่จำกัด",
        },
        ["settings.cliDailyBudget"] = new() { ["en"] = "CLI agents: budget per day (USD)", ["th"] = "เอเจนต์ CLI: งบต่อวัน (ดอลลาร์)" },
        ["settings.cliDailyBudgetDesc"] = new()
        {
            ["en"] = "Per CLI, API-equivalent. Once reached, no new turn starts on it today. 0 = no limit.",
            ["th"] = "แยกตาม CLI คิดเป็นมูลค่าเทียบ API ถึงแล้ววันนี้จะไม่เริ่มเทิร์นใหม่ ใส่ 0 = ไม่จำกัด",
        },
        ["agent.subAgentsUnfinished"] = new()
        {
            ["en"] = "{0} sub-agent(s) did not finish, so this task is not complete: {1}",
            ["th"] = "มีเอเจนต์ย่อย {0} ตัวทำงานไม่จบ งานนี้จึงยังไม่เสร็จ: {1}",
        },
        ["agent.cliBudgetStopped"] = new()
        {
            ["en"] = "The turn stopped at this run's budget (Settings ▸ Agent), so the task may be unfinished. Say \"continue\" to carry on.",
            ["th"] = "เทิร์นนี้หยุดเพราะถึงงบต่อรอบแล้ว (การตั้งค่า ▸ เอเจนต์) งานอาจยังไม่เสร็จ พิมพ์ \"ทำต่อ\" เพื่อทำต่อได้",
        },
        ["agent.worktreesKept"] = new()
        {
            ["en"] = "{0} sub-agent worktree(s) still hold work, nothing was deleted: {1}",
            ["th"] = "มี worktree ของเอเจนต์ย่อย {0} ชุดที่ยังมีงานค้าง ฉันไม่ได้ลบ: {1}",
        },
        ["agent.cliBudgetSpent"] = new()
        {
            ["en"] = "Today's budget for this CLI agent is used up (Settings ▸ Agent). Nothing was started.",
            ["th"] = "งบวันนี้ของเอเจนต์ CLI นี้หมดแล้ว (การตั้งค่า ▸ เอเจนต์) จึงยังไม่ได้เริ่มงาน",
        },
        ["settings.specFix"] = new() { ["en"] = "Speculative fix", ["th"] = "ส่งตัวแก้แข่งกัน (speculative fix)" },
        ["settings.specFixDesc"] = new()
        {
            ["en"] = "When a build or test stays red after the agent's own fix, several fixers try different approaches in their own "
                   + "worktrees; the first one that turns the command green is applied, the rest are dropped.",
            ["th"] = "เมื่อ build หรือเทสต์ยังแดงหลังเอเจนต์แก้เองแล้ว จะส่งตัวแก้หลายตัวลองคนละแนวทางใน worktree ของตัวเอง "
                   + "ตัวแรกที่ทำให้คำสั่งผ่านจะถูกนำมาใช้ ที่เหลือทิ้งไป",
        },
        ["settings.specFixCount"] = new() { ["en"] = "Fixers per race", ["th"] = "จำนวนตัวแก้ต่อรอบ" },
        ["settings.specFixCountDesc"] = new() { ["en"] = "2 or 3 (never more than max parallel).", ["th"] = "2 หรือ 3 ตัว (ไม่เกินจำนวนทำพร้อมกันสูงสุด)" },
        ["settings.specFixMinutes"] = new() { ["en"] = "Race time limit (minutes)", ["th"] = "เวลาจำกัดต่อรอบ (นาที)" },
        ["settings.specFixMinutesDesc"] = new()
        {
            ["en"] = "No fixer green in time → the main agent carries on with what they found.",
            ["th"] = "ถ้าไม่มีตัวไหนผ่านภายในเวลา เอเจนต์หลักทำต่อพร้อมสิ่งที่ตัวแก้ค้นพบ",
        },
        ["settings.modelOverrides"] = new() { ["en"] = "Model per sub-agent kind", ["th"] = "โมเดลของเอเจนต์ย่อยแต่ละแบบ" },
        ["settings.modelOverridesDesc"] = new()
        {
            ["en"] = "Leave empty for the automatic pick: explore → the provider's cheapest (Haiku-class), code and review → the run's "
                   + "model, a fix two fixers missed → one step up (Opus-class). A model id of the active provider.",
            ["th"] = "เว้นว่าง = เลือกให้อัตโนมัติ: สำรวจ → โมเดลถูกสุดของ provider (ระดับ Haiku) · เขียนโค้ดและตรวจ → โมเดลเดียวกับงานหลัก · "
                   + "แก้ที่ตัวแก้สองตัวทำไม่ได้ → ขึ้นหนึ่งขั้น (ระดับ Opus) · ใส่ model id ของ provider ที่ใช้อยู่",
        },
        ["settings.modelExplore"] = new() { ["en"] = "Explore (read-only)", ["th"] = "สำรวจ (อ่านอย่างเดียว)" },
        ["settings.modelCode"] = new() { ["en"] = "Code (workers, fixers)", ["th"] = "เขียนโค้ด (ผู้ทำงาน, ตัวแก้)" },
        ["settings.modelReview"] = new() { ["en"] = "Review", ["th"] = "ตรวจงาน" },
        ["settings.modelHardFix"] = new() { ["en"] = "Hard fix (after two failed fixers)", ["th"] = "แก้ยาก (หลังตัวแก้พลาดสองตัว)" },

        // ── Spend window: per strategy ──
        ["usage.strategyTitle"] = new() { ["en"] = "By dispatch strategy · 14 days", ["th"] = "แยกตามวิธีแบ่งงาน · 14 วัน" },
        ["usage.strategyDesc"] = new()
        {
            ["en"] = "What each kind of agent run cost, how long it took and how often it finished — to tune when to fan out.",
            ["th"] = "งานแต่ละแบบเสียเงินเท่าไหร่ ใช้เวลานานแค่ไหน และสำเร็จบ่อยแค่ไหน — ใช้ปรับว่าควรแยกงานเมื่อไหร่",
        },
        ["usage.strategyNone"] = new() { ["en"] = "No agent runs recorded yet.", ["th"] = "ยังไม่มีรอบงานของเอเจนต์ที่บันทึกไว้" },
        ["usage.strategyCol"] = new() { ["en"] = "Strategy", ["th"] = "วิธี" },
        ["usage.strategyRuns"] = new() { ["en"] = "Runs", ["th"] = "รอบ" },
        ["usage.strategySuccess"] = new() { ["en"] = "Success", ["th"] = "สำเร็จ" },
        ["usage.strategyPerRun"] = new() { ["en"] = "Per run", ["th"] = "ต่อรอบ" },
        ["usage.strategyTime"] = new() { ["en"] = "Time", ["th"] = "เวลา" },
        ["usage.strategyTotal"] = new() { ["en"] = "Total", ["th"] = "รวม" },
        ["usage.strategy.single"] = new() { ["en"] = "Single agent", ["th"] = "เอเจนต์เดียว" },
        ["usage.strategy.fan-out"] = new() { ["en"] = "Read-only fan-out", ["th"] = "แยกสำรวจขนาน" },
        ["usage.strategy.workers"] = new() { ["en"] = "Workers (worktrees)", ["th"] = "ผู้ทำงานใน worktree" },
        ["usage.strategy.speculative-fix"] = new() { ["en"] = "Speculative fix", ["th"] = "ตัวแก้แข่งกัน" },
    };
}
