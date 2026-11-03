namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Billing page's Savings &amp; comparison section and its credit / plan editor
/// (Views/Billing/SavingsPanel.xaml, AccountPanel.xaml; they moved there from the old spend window, whose "usage.*"
/// keys they still use). Every key needs both "en" and "th".</summary>
internal static class BillingSavingsKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["billing.savings.title"] = new() { ["en"] = "Savings & comparison", ["th"] = "เงินที่ประหยัดได้และการเปรียบเทียบ" },
        ["billing.savings.subtitle"] = new()
        {
            ["en"] = "What was paid, and what the same work would have cost without CluadeX's savings — by provider, over the dates you choose.",
            ["th"] = "จ่ายจริงไปเท่าไร และถ้าไม่มีระบบประหยัดของ CluadeX งานเดียวกันจะเสียเท่าไร — แยกตามผู้ให้บริการ ในช่วงวันที่ที่เลือก",
        },
        ["billing.savings.onlyTab"] = new()
        {
            ["en"] = "Showing {0} only. Open the All tab to pick and compare several providers.",
            ["th"] = "แสดงเฉพาะ {0} เปิดแท็บทั้งหมดเพื่อเลือกและเปรียบเทียบหลายผู้ให้บริการ",
        },
        ["billing.savings.paidRunning"] = new() { ["en"] = "Paid, running total", ["th"] = "จ่ายจริงสะสม" },
        ["billing.savings.strategyTitle"] = new() { ["en"] = "By dispatch strategy · selected dates", ["th"] = "แยกตามวิธีแบ่งงาน · ช่วงวันที่ที่เลือก" },
        ["billing.account.creditTitle"] = new() { ["en"] = "{0} · credit", ["th"] = "{0} · เครดิต" },
        ["billing.account.planTitle"] = new() { ["en"] = "{0} · plan price", ["th"] = "{0} · ราคาแพ็กเกจ" },
        ["billing.account.replaceNote"] = new()
        {
            ["en"] = "Setting a new balance replaces the one entered before and counts down from now.",
            ["th"] = "การบันทึกยอดใหม่จะแทนยอดที่กรอกไว้เดิม และเริ่มหักจากตอนนี้",
        },
        ["billing.account.clearCreditAsk"] = new()
        {
            ["en"] = "Clear the credit entered for {0}? Its countdown stops; you can enter it again later.",
            ["th"] = "ล้างเครดิตที่กรอกไว้ของ {0} หรือไม่? จะหยุดหักยอด และกรอกใหม่ได้ภายหลัง",
        },
        ["billing.account.clearPlanAsk"] = new()
        {
            ["en"] = "Clear the plan price entered for {0}? The charts go back to its API-equivalent estimate.",
            ["th"] = "ล้างราคาแพ็กเกจที่กรอกไว้ของ {0} หรือไม่? กราฟจะกลับไปใช้ค่าเทียบ API แทน",
        },
    };
}
