namespace CluadeX.Services.Localization;

/// <summary>Localized strings for sub-agent tiers (auto model: cheap / mid / deep): Settings ▸ Agent, the Sub-agents
/// page's tier picker, the agent map's tier badge and Upgrade button, and the savings page's per-tier lines. Every key
/// needs both "en" and "th".</summary>
internal static class TierKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── tier names (badge, picker, savings) ──
        ["usage.tier.cheap"] = new() { ["en"] = "cheap", ["th"] = "ประหยัด" },
        ["usage.tier.mid"] = new() { ["en"] = "mid", ["th"] = "กลาง" },
        ["usage.tier.deep"] = new() { ["en"] = "deep", ["th"] = "เต็มกำลัง" },

        // ── Billing ▸ savings ──
        ["usage.byRouting"] = new()
        {
            ["en"] = "Sub-agent tiers — helpers on a cheaper model than the run's (redone work subtracted)",
            ["th"] = "ระดับเอเจนต์ย่อย — ผู้ช่วยใช้โมเดลที่ถูกกว่าโมเดลหลัก (หักงานที่ต้องทำซ้ำแล้ว)",
        },
        ["usage.tierTitle"] = new() { ["en"] = "Sub-agents by tier", ["th"] = "เอเจนต์ย่อยแยกตามระดับ" },
        ["usage.tierDesc"] = new()
        {
            ["en"] = "What each tier paid against the run's own model. A cheap attempt that fell short and was redone one tier up counts as a loss, so the number is the real saving.",
            ["th"] = "แต่ละระดับจ่ายไปเท่าไหร่ เทียบกับถ้าใช้โมเดลหลัก รอบที่ทำไม่ผ่านแล้วต้องขึ้นไปทำซ้ำระดับบนนับเป็นขาดทุน ตัวเลขนี้จึงเป็นเงินที่ประหยัดได้จริง",
        },
        ["usage.tierRequests"] = new() { ["en"] = "requests", ["th"] = "คำขอ" },
        ["usage.tierPaid"] = new() { ["en"] = "paid", ["th"] = "จ่ายจริง" },
        ["usage.tierOnRunModel"] = new() { ["en"] = "on the run's model", ["th"] = "ถ้าใช้โมเดลหลัก" },
        ["usage.tierClimbs"] = new() { ["en"] = "redone one tier up:", ["th"] = "ต้องขึ้นไปทำซ้ำระดับบน:" },

        // ── Settings ▸ Agent ▸ dispatch card ──
        ["settings.tiers"] = new() { ["en"] = "Sub-agent model (auto)", ["th"] = "โมเดลของเอเจนต์ย่อย (อัตโนมัติ)" },
        ["settings.tiersDesc"] = new()
        {
            ["en"] = "Each sub-agent gets the cheapest model that fits its work: reading and searching on the cheapest, editing on the run's own. "
                   + "The main chat never switches model, so its prompt cache stays warm.",
            ["th"] = "เอเจนต์ย่อยแต่ละตัวได้โมเดลที่ถูกที่สุดที่ยังพอกับงาน: อ่าน/ค้นหาใช้ตัวถูกที่สุด งานแก้ไขใช้โมเดลหลัก "
                   + "แชทหลักไม่เปลี่ยนโมเดลเลย prompt cache จึงไม่หลุด",
        },
        ["settings.autoModel"] = new() { ["en"] = "Auto model for sub-agents", ["th"] = "เลือกโมเดลเอเจนต์ย่อยอัตโนมัติ" },
        ["settings.autoModelDesc"] = new()
        {
            ["en"] = "Off: every sub-agent runs on the run's model (your per-kind overrides above still apply).",
            ["th"] = "ปิด: เอเจนต์ย่อยทุกตัวใช้โมเดลหลักแบบเดิม (ค่าที่ตั้งแยกตามชนิดด้านบนยังใช้อยู่)",
        },
        ["settings.tierEscalate"] = new() { ["en"] = "Redo a weak result one tier up", ["th"] = "ผลงานอ่อน ให้ขึ้นไปทำใหม่ระดับบน" },
        ["settings.tierEscalateDesc"] = new()
        {
            ["en"] = "An empty, very short or \"could not find\" report from a cheap/mid agent is redone one tier up (twice at most), "
                   + "carrying what it already found. A kind that keeps climbing starts one tier higher next time.",
            ["th"] = "รายงานว่าง สั้นมาก หรือบอกว่า \"หาไม่พบ\" จากเอเจนต์ระดับประหยัด/กลาง จะถูกส่งขึ้นไปทำใหม่ระดับบน (ไม่เกิน 2 ครั้ง) "
                   + "พร้อมสิ่งที่หาเจอแล้ว ชนิดไหนต้องขึ้นบ่อย ครั้งหน้าจะเริ่มที่ระดับสูงขึ้นเลย",
        },
        ["settings.tierLocalCheap"] = new() { ["en"] = "Use a loaded local model as the cheap tier", ["th"] = "ใช้โมเดลในเครื่องที่โหลดไว้เป็นระดับประหยัด" },
        ["settings.tierLocalCheapDesc"] = new()
        {
            ["en"] = "Only a model that is already loaded, can call tools and is 7B or larger, and only for read-only work. Nothing is loaded for it.",
            ["th"] = "ใช้เฉพาะโมเดลที่โหลดอยู่แล้ว เรียกเครื่องมือได้ และขนาด 7B ขึ้นไป และเฉพาะงานอ่านอย่างเดียว ไม่โหลดโมเดลเพิ่มให้",
        },
        ["settings.modelCheap"] = new() { ["en"] = "Cheap tier model (empty = the provider's cheapest)", ["th"] = "โมเดลระดับประหยัด (ว่าง = ตัวถูกที่สุดของผู้ให้บริการ)" },
        ["settings.modelMid"] = new() { ["en"] = "Mid tier model (empty = one class below the run's)", ["th"] = "โมเดลระดับกลาง (ว่าง = ต่ำกว่าโมเดลหลักหนึ่งขั้น)" },
        ["settings.modelDeep"] = new() { ["en"] = "Deep tier model (empty = the run's own)", ["th"] = "โมเดลระดับเต็มกำลัง (ว่าง = โมเดลหลัก)" },

        // ── Sub-agents page ──
        ["subag.tierTitle"] = new() { ["en"] = "TIER (AUTO MODEL)", ["th"] = "ระดับ (โมเดลอัตโนมัติ)" },
        ["subag.tierDesc"] = new()
        {
            ["en"] = "Which model class this agent runs on when auto model is on. Unsure? Pick mid: a cheap attempt that falls short is paid for twice.",
            ["th"] = "เอเจนต์ตัวนี้ใช้โมเดลระดับไหนเมื่อเปิดโหมดอัตโนมัติ ถ้าไม่แน่ใจให้เลือกกลาง เพราะถ้าระดับประหยัดทำไม่ผ่านจะเสียเงินสองรอบ",
        },
        ["subag.tierFile"] = new() { ["en"] = "Its file's tier", ["th"] = "ตามไฟล์ของเอเจนต์" },
        ["subag.tierFileIs"] = new() { ["en"] = "Its file's tier ({0})", ["th"] = "ตามไฟล์ของเอเจนต์ ({0})" },
        ["subag.tierSaved"] = new() { ["en"] = "Saved the tier for {0}", ["th"] = "บันทึกระดับของ {0} แล้ว" },
        ["subag.tierSaveFailed"] = new() { ["en"] = "Could not save the tier — the settings file did not write", ["th"] = "บันทึกระดับไม่สำเร็จ — เขียนไฟล์ตั้งค่าไม่ได้" },

        // ── agent window ──
        ["agentdetail.upgrade"] = new() { ["en"] = "Upgrade", ["th"] = "อัปเกรด" },
        ["agentdetail.upgrade.tip"] = new()
        {
            ["en"] = "Move this agent to the run's own model from its next step (what it did so far carries over)",
            ["th"] = "ย้ายเอเจนต์ตัวนี้ไปใช้โมเดลหลักตั้งแต่ขั้นถัดไป (งานที่ทำมาแล้วยังอยู่)",
        },
    };
}
