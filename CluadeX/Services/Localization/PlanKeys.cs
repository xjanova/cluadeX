namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the plan card under the agent's reply (todo_write / update_plan): its scope chip, the
/// stopped header and the stopped step. Every key has "en" and "th".</summary>
internal static class PlanKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["plan.card.stopped"] = new() { ["en"] = "Stopped ({0}/{1})", ["th"] = "หยุดไว้ ({0}/{1})" },
        ["plan.step.stopped"] = new() { ["en"] = "stopped here", ["th"] = "หยุดที่ขั้นนี้" },
        ["plan.scope.first_task"] = new() { ["en"] = "First task", ["th"] = "งานแรก" },
        ["plan.scope.continuation"] = new() { ["en"] = "Continuation", ["th"] = "ต่อจากเดิม" },
        ["plan.scope.new_task"] = new() { ["en"] = "New task", ["th"] = "งานใหม่" },
        ["plan.scope.tip"] = new() { ["en"] = "What this request is to the chat, as the agent sees it: the chat's first task, a continuation of the work here, or a new, unrelated task", ["th"] = "คำขอนี้เป็นงานแบบไหนสำหรับแชทนี้ ตามที่เอเจนต์มอง: งานแรกของแชท งานต่อจากเดิม หรืองานใหม่ที่ไม่เกี่ยวกับงานก่อนหน้า" },
    };
}
