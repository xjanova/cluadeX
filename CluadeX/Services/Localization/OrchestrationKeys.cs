namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the orchestration views (docs/agent-orchestration.md): the plan's "running" line, the
/// workflow card, the Ultra switch, split-chat chips, background notices, the next-step box. Every key has "en" and "th".</summary>
internal static class OrchestrationKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── under the plan's active step ───
        ["plan.running"] = new() { ["en"] = "Running: {0}", ["th"] = "กำลังรัน: {0}" },
        ["plan.running.subAgent"] = new() { ["en"] = "{0} sub-agent", ["th"] = "เอเจนต์ย่อย {0}" },
        ["plan.running.subAgents"] = new() { ["en"] = "{0} sub-agents", ["th"] = "เอเจนต์ย่อย {0}" },
        ["plan.running.workflow"] = new() { ["en"] = "{0} workflow", ["th"] = "เวิร์กโฟลว์ {0}" },
        ["plan.running.workflows"] = new() { ["en"] = "{0} workflows", ["th"] = "เวิร์กโฟลว์ {0}" },
        ["plan.running.bg"] = new() { ["en"] = "{0} background command", ["th"] = "คำสั่งเบื้องหลัง {0}" },
        ["plan.running.bgs"] = new() { ["en"] = "{0} background commands", ["th"] = "คำสั่งเบื้องหลัง {0}" },

        // ─── the workflow card ───
        ["orch.wf.agents"] = new() { ["en"] = "{0} agents", ["th"] = "{0} เอเจนต์" },
        ["orch.wf.status.running"] = new() { ["en"] = "Running", ["th"] = "กำลังทำงาน" },
        ["orch.wf.status.done"] = new() { ["en"] = "Done", ["th"] = "เสร็จแล้ว" },
        ["orch.wf.status.stopped"] = new() { ["en"] = "Stopped", ["th"] = "หยุดแล้ว" },
        ["orch.wf.status.failed"] = new() { ["en"] = "Failed", ["th"] = "ล้มเหลว" },
        ["orch.wf.stop"] = new() { ["en"] = "Stop", ["th"] = "หยุด" },
        ["orch.wf.resume"] = new() { ["en"] = "Resume", ["th"] = "ทำต่อ" },
        ["orch.wf.stopTip"] = new() { ["en"] = "Stop every agent of this workflow (what finished is kept for Resume)", ["th"] = "หยุดเอเจนต์ทุกตัวของเวิร์กโฟลว์นี้ (งานที่เสร็จแล้วเก็บไว้ให้กดทำต่อ)" },
        ["orch.wf.resumeTip"] = new() { ["en"] = "Continue: finished agents are reused, only the rest run", ["th"] = "ทำต่อ: ใช้ผลของเอเจนต์ที่เสร็จแล้วซ้ำ รันเฉพาะที่เหลือ" },
        ["orch.wf.expandTip"] = new() { ["en"] = "Show the phases and their agents", ["th"] = "ดูขั้นตอนและเอเจนต์ในแต่ละขั้น" },
        ["orch.wf.estimate"] = new() { ["en"] = "Estimate: up to {0} agents, {1} at once · ≈ ${2:0.00} · ≈ {3:0.0} GB RAM", ["th"] = "ประมาณการ: สูงสุด {0} เอเจนต์ ครั้งละ {1} · ≈ ${2:0.00} · RAM ≈ {3:0.0} GB" },
        ["orch.wf.spent"] = new() { ["en"] = "spent ${0:0.00}", ["th"] = "ใช้ไป ${0:0.00}" },
        ["orch.wf.verdicts"] = new() { ["en"] = "verify: {0} kept · {1} refuted", ["th"] = "ตรวจค้าน: ผ่าน {0} · ถูกตีตก {1}" },
        ["orch.wf.appClosed"] = new() { ["en"] = "Stopped when the app closed — Resume continues from what finished.", ["th"] = "หยุดเพราะแอปถูกปิด — กดทำต่อเพื่อทำจากส่วนที่เสร็จแล้ว" },
        ["orch.wf.endedNote"] = new() { ["en"] = "Workflow \"{0}\" ended: {1}", ["th"] = "เวิร์กโฟลว์ \"{0}\" จบแล้ว: {1}" },
        ["orch.wf.row.queued"] = new() { ["en"] = "queued", ["th"] = "รอคิว" },
        ["orch.wf.row.running"] = new() { ["en"] = "running", ["th"] = "กำลังทำ" },
        ["orch.wf.row.done"] = new() { ["en"] = "done", ["th"] = "เสร็จ" },
        ["orch.wf.row.failed"] = new() { ["en"] = "failed", ["th"] = "ล้มเหลว" },
        ["orch.wf.row.stopped"] = new() { ["en"] = "stopped", ["th"] = "หยุด" },
        ["orch.wf.row.skipped"] = new() { ["en"] = "skipped (budget)", ["th"] = "ข้าม (เกินงบ)" },
        ["orch.wf.row.cached"] = new() { ["en"] = "reused", ["th"] = "ใช้ผลเดิม" },
        ["orch.wf.row.checking"] = new() { ["en"] = "checking (gate)", ["th"] = "กำลังตรวจ (build/test)" },
        ["orch.wf.gate.green"] = new() { ["en"] = "gate ✓", ["th"] = "ผ่านการตรวจ ✓" },
        ["orch.wf.gate.red"] = new() { ["en"] = "gate ✗", ["th"] = "ตรวจไม่ผ่าน ✗" },
        ["orch.wf.merge"] = new() { ["en"] = "merge: {0} merged · {1} left out", ["th"] = "รวมงาน: รวมแล้ว {0} · ไม่ได้รวม {1}" },
        ["orch.wf.tests.green"] = new() { ["en"] = "tests on the merged result ✓", ["th"] = "เทสต์บนงานที่รวมแล้วผ่าน ✓" },
        ["orch.wf.tests.red"] = new() { ["en"] = "tests on the merged result ✗", ["th"] = "เทสต์บนงานที่รวมแล้วไม่ผ่าน ✗" },
        ["orch.wf.transcript.prompt"] = new() { ["en"] = "Prompt", ["th"] = "คำสั่งที่ได้รับ" },
        ["orch.wf.transcript.answer"] = new() { ["en"] = "Answer", ["th"] = "คำตอบ" },
        ["orch.wf.transcript.error"] = new() { ["en"] = "Error", ["th"] = "ข้อผิดพลาด" },
        ["orch.wf.rowTip"] = new() { ["en"] = "Show this agent's prompt and answer", ["th"] = "ดูคำสั่งและคำตอบของเอเจนต์นี้" },

        // ─── the Ultra switch ───
        ["orch.ultra"] = new() { ["en"] = "Ultra", ["th"] = "อัลตร้า" },
        // ─── the composer's switch: read-only sub-agents on a cheap API model, or on the model on this PC (Colibri) ───
        ["orch.localAgents"] = new() { ["en"] = "Colibri", ["th"] = "Colibri" },
        ["orch.localAgentsTip"] = new() { ["en"] = "On: read-only sub-agents run on the model already loaded on this PC (Colibri, llama-server, Ollama) — free, slower, for work that can wait. Only when that model can do the job (7B+; 30B+ for mid-tier work), never one that would have to load, and only while the server has a free slot; otherwise they use a cheap API model. Edits always stay on the API. Off: a cheap API model.", ["th"] = "เปิด: เอเจนต์ย่อยที่อ่านอย่างเดียวไปใช้โมเดลที่โหลดอยู่บนเครื่องนี้ (Colibri, llama-server, Ollama) — ฟรี ช้ากว่า สำหรับงานที่ไม่เร่ง เฉพาะเมื่อโมเดลนั้นทำงานนี้ได้จริง (7B ขึ้นไป งานระดับกลาง 30B ขึ้นไป) ไม่โหลดโมเดลใหม่ และเฉพาะตอนที่ server มีช่องว่าง ไม่งั้นใช้โมเดล API ราคาถูก งานแก้ไฟล์ใช้ API เสมอ ปิด: ใช้โมเดล API ราคาถูก" },
        ["orch.ultraTip"] = new() { ["en"] = "Ultra: the agent may run multi-agent workflows in this chat (many agents, many more tokens). Off by default — asking for a workflow in your message also allows one for that turn.", ["th"] = "อัลตร้า: ให้เอเจนต์รันเวิร์กโฟลว์หลายเอเจนต์ในแชทนี้ได้ (ใช้เอเจนต์และโทเค็นมากขึ้นมาก) ปิดไว้เป็นค่าเริ่มต้น — ถ้าพิมพ์ขอเวิร์กโฟลว์ในข้อความก็ใช้ได้เฉพาะรอบนั้น" },

        // ─── split-chat chips ───
        ["orch.splits.header"] = new() { ["en"] = "Split chats", ["th"] = "แชทงานแยก" },
        ["orch.splits.working"] = new() { ["en"] = "working", ["th"] = "กำลังทำ" },
        ["orch.splits.waiting"] = new() { ["en"] = "waiting for you", ["th"] = "รอคุณตอบ" },
        ["orch.splits.done"] = new() { ["en"] = "done", ["th"] = "เสร็จ" },
        ["orch.splits.idle"] = new() { ["en"] = "idle", ["th"] = "ว่าง" },
        ["orch.splits.openTip"] = new() { ["en"] = "Open this chat", ["th"] = "เปิดแชทนี้" },
        ["orch.splits.turnNote"] = new() { ["en"] = "Split chat \"{0}\" ended a turn: {1}", ["th"] = "งานแยก \"{0}\" จบรอบแล้ว: {1}" },

        // ─── background command notices ───
        ["orch.bg.one"] = new() { ["en"] = "Background command finished: {0} · {1} · {2}", ["th"] = "คำสั่งเบื้องหลังจบแล้ว: {0} · {1} · {2}" },
        ["orch.bg.many"] = new() { ["en"] = "{0} background commands finished", ["th"] = "คำสั่งเบื้องหลังจบแล้ว {0} รายการ" },
        ["orch.bg.exit"] = new() { ["en"] = "exit {0}", ["th"] = "รหัสจบ {0}" },
        ["orch.bg.stopped"] = new() { ["en"] = "stopped", ["th"] = "ถูกหยุด" },

        // ─── the next-step box ───
        ["orch.next.title"] = new() { ["en"] = "Next step", ["th"] = "ขั้นต่อไป" },
        ["orch.next.picked"] = new() { ["en"] = "You chose: {0}", ["th"] = "คุณเลือก: {0}" },
    };
}
