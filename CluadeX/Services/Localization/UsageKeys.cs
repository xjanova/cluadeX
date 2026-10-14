namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the spend window (what CluadeX spent, and what its savings saved). Every key needs both
/// "en" and "th".</summary>
internal static class UsageKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["usage.menu"] = new() { ["en"] = "Spend & savings chart…", ["th"] = "กราฟค่าใช้จ่ายและเงินที่ประหยัดได้…" },
        ["usage.title"] = new() { ["en"] = "Spend & savings", ["th"] = "ค่าใช้จ่ายและเงินที่ประหยัดได้" },
        ["usage.subtitle"] = new()
        {
            ["en"] = "What CluadeX spent on paid models, and what the same work would have cost without its savings.",
            ["th"] = "CluadeX ใช้เงินกับโมเดลแบบเสียเงินไปเท่าไหร่ และถ้าไม่มีระบบประหยัด งานเดียวกันจะเสียเท่าไหร่",
        },
        ["usage.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["usage.today"] = new() { ["en"] = "Today", ["th"] = "วันนี้" },
        ["usage.week"] = new() { ["en"] = "Last 7 days", ["th"] = "7 วันล่าสุด" },
        ["usage.month"] = new() { ["en"] = "Last 30 days", ["th"] = "30 วันล่าสุด" },
        ["usage.paid"] = new() { ["en"] = "Paid {0}", ["th"] = "จ่ายจริง {0}" },
        ["usage.without"] = new() { ["en"] = "Without savings ≈ {0}", ["th"] = "ถ้าไม่ประหยัด ≈ {0}" },
        ["usage.saved"] = new() { ["en"] = "Saved {0} ({1})", ["th"] = "ประหยัด {0} ({1})" },
        ["usage.nothingYet"] = new() { ["en"] = "No paid use yet", ["th"] = "ยังไม่มีการใช้แบบเสียเงิน" },
        ["usage.providerAll"] = new() { ["en"] = "All providers", ["th"] = "ทุก provider รวมกัน" },
        ["usage.providerOther"] = new() { ["en"] = "Other", ["th"] = "อื่น ๆ" },
        ["usage.providerLabel"] = new() { ["en"] = "Show", ["th"] = "แสดง" },
        ["usage.tokens"] = new()
        {
            ["en"] = "30 days: {0} tokens sent · {1} read from cache · {2} generated · {3} requests",
            ["th"] = "30 วัน: ส่งไป {0} โทเค็น · อ่านจากแคช {1} · ตอบออก {2} · {3} ครั้ง",
        },
        ["usage.lineTitle"] = new() { ["en"] = "Running total · 14 days", ["th"] = "ยอดสะสม · 14 วัน" },
        ["usage.lineSaved"] = new() { ["en"] = "{0} saved · {1}%", ["th"] = "ประหยัดสะสม {0} · {1}%" },
        ["usage.lineWithout"] = new() { ["en"] = "Without savings", ["th"] = "ถ้าไม่ประหยัด" },
        ["usage.lineTip"] = new()
        {
            ["en"] = "Up to {0}\nPaid {1}\nWithout savings ≈ {2}\nSaved {3}",
            ["th"] = "ถึง {0}\nจ่ายจริงสะสม {1}\nถ้าไม่ประหยัด ≈ {2}\nประหยัดสะสม {3}",
        },
        ["usage.chartTitle"] = new() { ["en"] = "Last 14 days", ["th"] = "14 วันล่าสุด" },
        ["usage.legendPaid"] = new() { ["en"] = "Paid", ["th"] = "จ่ายจริง" },
        ["usage.legendSaved"] = new() { ["en"] = "Saved (the rest of the bar = without savings)", ["th"] = "ประหยัดได้ (ทั้งแท่ง = ถ้าไม่ประหยัด)" },
        ["usage.barTip"] = new()
        {
            ["en"] = "{0}\nPaid {1} · {2} requests\nWithout savings ≈ {3}\nSaved {4}",
            ["th"] = "{0}\nจ่ายจริง {1} · {2} ครั้ง\nถ้าไม่ประหยัด ≈ {3}\nประหยัด {4}",
        },
        ["usage.byTitle"] = new() { ["en"] = "Where the 30 days' savings came from", ["th"] = "30 วันนี้ประหยัดได้จากอะไร" },
        ["usage.byCache"] = new() { ["en"] = "Prompt cache — repeated context read back at a fraction of the price", ["th"] = "Prompt cache — ส่วนที่ส่งซ้ำอ่านจากแคชในราคาถูก" },
        ["usage.byToolSearch"] = new() { ["en"] = "Tool search — tool definitions loaded only when needed", ["th"] = "Tool search — โหลดคำอธิบายเครื่องมือเฉพาะตอนต้องใช้" },
        ["usage.byCompaction"] = new() { ["en"] = "Microcompact — old tool results shortened", ["th"] = "Microcompact — ย่อผลลัพธ์เครื่องมือเก่า" },
        ["usage.byBatch"] = new() { ["en"] = "Batch — background work at half price", ["th"] = "Batch — งานเบื้องหลังราคาครึ่งเดียว" },
        ["usage.balanceTitle"] = new() { ["en"] = "Credit left (estimate)", ["th"] = "เครดิตคงเหลือ (ประมาณ)" },
        ["usage.balanceValue"] = new() { ["en"] = "≈ {0}", ["th"] = "≈ {0}" },
        ["usage.balanceSince"] = new()
        {
            ["en"] = "{0} entered {1}, minus {2} CluadeX spent since",
            ["th"] = "กรอกไว้ {0} เมื่อ {1} หักที่ CluadeX ใช้ไปตั้งแต่นั้น {2}",
        },
        ["usage.balanceNone"] = new()
        {
            ["en"] = "Providers have no API for the prepaid balance. Enter the balance from the billing page and CluadeX counts down from it.",
            ["th"] = "ผู้ให้บริการไม่มี API บอกยอดเครดิตคงเหลือ กรอกยอดจากหน้า billing แล้ว CluadeX จะหักค่าใช้จ่ายให้เอง",
        },
        ["usage.balanceInput"] = new() { ["en"] = "Balance now (USD)", ["th"] = "ยอดตอนนี้ (USD)" },
        ["usage.balanceSave"] = new() { ["en"] = "Set", ["th"] = "บันทึก" },
        ["usage.balanceClear"] = new() { ["en"] = "Clear", ["th"] = "ล้าง" },
        ["usage.balanceBad"] = new() { ["en"] = "Enter a number such as 25 or 12.50", ["th"] = "กรอกเป็นตัวเลข เช่น 25 หรือ 12.50" },
        ["usage.billing"] = new() { ["en"] = "Open the billing page", ["th"] = "เปิดหน้า billing" },
        ["usage.note"] = new()
        {
            ["en"] = "List prices × the token counts each API response reports. \"Without savings\" is an estimate: every prompt token at the plain input price, the tokens tool search and microcompact kept out sent as well, and no batch discount. Days before this chart existed show the paid amount only.",
            ["th"] = "ราคาตามรายการ × จำนวนโทเค็นที่ API ส่งกลับทุกครั้ง ส่วน \"ถ้าไม่ประหยัด\" เป็นค่าประมาณ: คิดทุกโทเค็นของ prompt เป็นราคาขาเข้าเต็ม รวมโทเค็นที่ tool search และ microcompact ตัดออก และไม่มีส่วนลด batch วันที่ก่อนมีกราฟนี้จะแสดงแค่ยอดที่จ่าย",
        },
    };
}
