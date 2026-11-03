namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Billing &amp; cost page (Views/BillingView.xaml, ViewModels/BillingViewModel.cs).
/// Every key needs both "en" and "th".</summary>
internal static class BillingKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["nav.billingTip"] = new() { ["en"] = "Billing & cost — spend and tokens per provider", ["th"] = "บิลและค่าใช้จ่าย — ยอดใช้จ่ายและโทเค็นแยกตามผู้ให้บริการ" },
        ["billing.title"] = new() { ["en"] = "Billing & cost", ["th"] = "บิลและค่าใช้จ่าย" },
        ["billing.subtitle"] = new() { ["en"] = "What every provider cost and used — per day, per model, per chat. Days follow this PC's time zone.",
            ["th"] = "แต่ละผู้ให้บริการเสียเงินและใช้โทเค็นไปเท่าไร — รายวัน รายโมเดล รายแชท วันนับตามเขตเวลาของเครื่องนี้" },
        ["billing.refresh"] = new() { ["en"] = "Refresh", ["th"] = "รีเฟรช" },
        ["billing.updated"] = new() { ["en"] = "Updated {0}", ["th"] = "อัปเดต {0}" },
        ["billing.range.days"] = new() { ["en"] = "{0} days", ["th"] = "{0} วัน" },
        ["billing.tab.all"] = new() { ["en"] = "All", ["th"] = "ทั้งหมด" },
        ["billing.noUseMonth"] = new() { ["en"] = "No use this month", ["th"] = "เดือนนี้ยังไม่ได้ใช้" },
        ["billing.noUse"] = new() { ["en"] = "No use", ["th"] = "ยังไม่มีการใช้" },
        ["billing.tokensWord"] = new() { ["en"] = "tokens", ["th"] = "โทเค็น" },
        ["billing.requests"] = new() { ["en"] = "{0} requests", ["th"] = "{0} คำขอ" },
        ["billing.tokLine"] = new() { ["en"] = "{0} in · {1} out", ["th"] = "เข้า {0} · ออก {1}" },

        ["billing.kind.all"] = new() { ["en"] = "API spend is billed at list price; ≈ marks subscription CLIs estimated at API prices; local models are free",
            ["th"] = "ค่า API คิดตามราคาประกาศ; ≈ คือ CLI แบบสมาชิกที่ประเมินเป็นราคา API; โมเดลบนเครื่องไม่มีค่าใช้จ่าย" },
        ["billing.kind.api"] = new() { ["en"] = "Billed per token — the tokens the API reported, at its list price",
            ["th"] = "คิดเงินตามโทเค็น — โทเค็นที่ API รายงานมา คูณราคาประกาศ" },
        ["billing.kind.subscription"] = new() { ["en"] = "On your plan, not billed per token — dollars marked ≈ are an estimate at API prices",
            ["th"] = "อยู่ในแพ็กเกจสมาชิกของคุณ ไม่ได้คิดเงินตามโทเค็น — ตัวเลขที่มี ≈ คือราคาประเมินเทียบราคา API" },
        ["billing.kind.local"] = new() { ["en"] = "Runs on this machine — $0; tokens and time only",
            ["th"] = "รันบนเครื่องนี้ — ไม่เสียเงิน แสดงเฉพาะโทเค็นและเวลา" },

        ["billing.period.today"] = new() { ["en"] = "Today", ["th"] = "วันนี้" },
        ["billing.period.week"] = new() { ["en"] = "This week", ["th"] = "สัปดาห์นี้" },
        ["billing.period.month"] = new() { ["en"] = "This month", ["th"] = "เดือนนี้" },
        ["billing.period.all"] = new() { ["en"] = "All time", ["th"] = "ทั้งหมดตั้งแต่เริ่ม" },

        ["billing.tokens.title"] = new() { ["en"] = "Tokens in the chart range", ["th"] = "โทเค็นในช่วงของกราฟ" },
        ["billing.tok.input"] = new() { ["en"] = "Input", ["th"] = "ขาเข้า" },
        ["billing.tok.output"] = new() { ["en"] = "Output", ["th"] = "ขาออก" },
        ["billing.tok.cacheRead"] = new() { ["en"] = "Cache read", ["th"] = "อ่านจากแคช" },
        ["billing.tok.cacheWrite"] = new() { ["en"] = "Cache write", ["th"] = "เขียนแคช" },
        ["billing.tok.thinking"] = new() { ["en"] = "Thinking", ["th"] = "การคิด" },
        ["billing.tok.time"] = new() { ["en"] = "Model time", ["th"] = "เวลาที่โมเดลใช้" },
        ["billing.tok.requests"] = new() { ["en"] = "Requests", ["th"] = "จำนวนคำขอ" },
        ["billing.tok.notReported"] = new() { ["en"] = "This provider does not report this count", ["th"] = "ผู้ให้บริการนี้ไม่ได้รายงานตัวเลขนี้" },

        ["billing.chart.spend"] = new() { ["en"] = "Spend per day", ["th"] = "ค่าใช้จ่ายรายวัน" },
        ["billing.chart.tokens"] = new() { ["en"] = "Tokens per day", ["th"] = "โทเค็นรายวัน" },
        ["billing.chart.cumulative"] = new() { ["en"] = "Running total", ["th"] = "ยอดสะสม" },
        ["billing.chart.totalSpend"] = new() { ["en"] = "{0} in this range", ["th"] = "{0} ในช่วงนี้" },
        ["billing.chart.totalTokens"] = new() { ["en"] = "{0} tokens in this range", ["th"] = "{0} โทเค็นในช่วงนี้" },
        ["billing.chart.tipSpend"] = new() { ["en"] = "{0}\n{1} · {2}\nRunning total {3}", ["th"] = "{0}\n{1} · {2}\nยอดสะสม {3}" },
        ["billing.chart.tipTokens"] = new() { ["en"] = "{0}\n{1} tokens · {2}\nRunning total {3}", ["th"] = "{0}\n{1} โทเค็น · {2}\nยอดสะสม {3}" },
        ["billing.chart.noUse"] = new() { ["en"] = "No use in this range", ["th"] = "ช่วงนี้ยังไม่มีการใช้" },

        ["billing.chip.billed"] = new() { ["en"] = "Billed", ["th"] = "คิดเงินจริง" },
        ["billing.chip.estimate"] = new() { ["en"] = "≈ Estimate", ["th"] = "≈ ประเมิน" },
        ["billing.chip.mixed"] = new() { ["en"] = "Billed + ≈ estimate", ["th"] = "คิดเงินจริง + ≈ ประเมิน" },
        ["billing.chip.free"] = new() { ["en"] = "Free · local", ["th"] = "ฟรี · บนเครื่อง" },

        ["billing.models.title"] = new() { ["en"] = "By model", ["th"] = "แยกตามโมเดล" },
        ["billing.models.none"] = new() { ["en"] = "No model used in this range", ["th"] = "ช่วงนี้ยังไม่มีการใช้โมเดล" },
        ["billing.model.unknown"] = new() { ["en"] = "(model not recorded)", ["th"] = "(ไม่ได้บันทึกชื่อโมเดล)" },
        ["billing.chats.title"] = new() { ["en"] = "Top chats by cost", ["th"] = "แชทที่ใช้จ่ายสูงสุด" },
        ["billing.chats.none"] = new() { ["en"] = "No chat in this range — usage outside a chat (titles, tests) still counts above",
            ["th"] = "ช่วงนี้ยังไม่มีแชท — การใช้นอกแชท (ตั้งชื่อแชท ทดสอบการเชื่อมต่อ) ยังนับรวมด้านบน" },
        ["billing.chat.untitled"] = new() { ["en"] = "(untitled chat)", ["th"] = "(แชทไม่มีชื่อ)" },
        ["billing.chat.gone"] = new() { ["en"] = "(deleted chat)", ["th"] = "(แชทที่ถูกลบแล้ว)" },
        ["billing.chat.subAgents"] = new() { ["en"] = "· sub-agents {0}", ["th"] = "· ซับเอเจนต์ {0}" },

        ["billing.budget.title"] = new() { ["en"] = "Budgets & limits", ["th"] = "งบประมาณและขีดจำกัด" },
        ["billing.budget.daily"] = new() { ["en"] = "Daily cap · API, all chats", ["th"] = "เพดานรายวัน · API ทุกแชท" },
        ["billing.budget.ofCap"] = new() { ["en"] = "{0} of {1} today", ["th"] = "วันนี้ {0} จาก {1}" },
        ["billing.budget.ofCapEst"] = new() { ["en"] = "≈ {0} of {1} today", ["th"] = "วันนี้ ≈ {0} จาก {1}" },
        ["billing.budget.run"] = new() { ["en"] = "Per-run cap · API", ["th"] = "เพดานต่อการรัน · API" },
        ["billing.budget.runAsk"] = new() { ["en"] = "asks before going past {0} in one run", ["th"] = "จะถามก่อนใช้เกิน {0} ในการรันครั้งเดียว" },
        ["billing.budget.runAskEst"] = new() { ["en"] = "asks before going past ≈ {0} in one run", ["th"] = "จะถามก่อนใช้เกิน ≈ {0} ในการรันครั้งเดียว" },
        ["billing.budget.credit"] = new() { ["en"] = "{0} credit", ["th"] = "เครดิต {0}" },
        ["billing.budget.creditLeft"] = new() { ["en"] = "{0} left of {1} (set {2})", ["th"] = "เหลือ {0} จาก {1} (ตั้งไว้ {2})" },
        ["billing.budget.cliDaily"] = new() { ["en"] = "Daily cap · {0}", ["th"] = "เพดานรายวัน · {0}" },
        ["billing.budget.cliRun"] = new() { ["en"] = "Per-run cap · CLI plans", ["th"] = "เพดานต่อการรัน · CLI แบบสมาชิก" },
        ["billing.budget.plan"] = new() { ["en"] = "{0} plan", ["th"] = "แพ็กเกจ {0}" },
        ["billing.budget.planFee"] = new() { ["en"] = "{0} a month since {1}", ["th"] = "{0} ต่อเดือน ตั้งแต่ {1}" },
        ["billing.budget.noneTitle"] = new() { ["en"] = "No budget set", ["th"] = "ยังไม่ได้ตั้งงบประมาณ" },
        ["billing.budget.none"] = new() { ["en"] = "Set a per-run or daily cap in Settings", ["th"] = "ตั้งเพดานต่อการรันหรือรายวันได้ที่หน้าตั้งค่า" },
        ["billing.budget.localTitle"] = new() { ["en"] = "No budget needed", ["th"] = "ไม่ต้องตั้งงบประมาณ" },
        ["billing.budget.local"] = new() { ["en"] = "Local models cost nothing per token", ["th"] = "โมเดลบนเครื่องไม่เสียเงินต่อโทเค็น" },

        ["billing.empty.title"] = new() { ["en"] = "No usage yet", ["th"] = "ยังไม่มีการใช้งาน" },
        ["billing.empty.body"] = new() { ["en"] = "Spend and tokens appear here, per provider, as soon as a model answers — API, a CLI on your plan, or a model on this machine.",
            ["th"] = "ยอดใช้จ่ายและโทเค็นจะแสดงที่นี่ แยกตามผู้ให้บริการ ทันทีที่โมเดลตอบ — ทั้ง API, CLI ในแพ็กเกจของคุณ หรือโมเดลบนเครื่องนี้" },
    };
}
