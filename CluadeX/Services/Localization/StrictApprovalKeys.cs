namespace CluadeX.Services.Localization;

/// <summary>Strict approval (Safety.StrictApproval): the Features switch, the one-time-code card and BrainX's approval
/// card. Every key needs "en" and "th".</summary>
internal static class StrictApprovalKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── Features ▸ Security ───
        ["feature.strictApproval"] = new() { ["en"] = "Strict approval for hard-to-undo commands", ["th"] = "อนุมัติแบบเข้มสำหรับคำสั่งที่ย้อนกลับยาก" },
        ["feature.strictApproval.desc"] = new()
        {
            ["en"] = "Deletes, prune, DROP / TRUNCATE, force push, reset --hard, format, registry and service changes, deploys and remote ssh with those words wait for a one-time code you type on the card (or approve from your own terminal). No rule, \"allow everything\" or AI message can approve them. Works for every provider; Codex refuses such commands in its sandbox instead.",
            ["th"] = "คำสั่งลบ, prune, DROP / TRUNCATE, force push, reset --hard, format, แก้ registry/service, deploy และ ssh ไปเครื่องอื่นด้วยคำสั่งเหล่านี้ จะรอให้คุณพิมพ์รหัสใช้ครั้งเดียวในการ์ด (หรืออนุมัติจากเทอร์มินัลของคุณเอง) กฎ, \"อนุญาตทั้งหมด\" หรือข้อความจาก AI อนุมัติแทนไม่ได้ ใช้ได้กับทุก provider ส่วน Codex จะปฏิเสธคำสั่งพวกนี้ใน sandbox แทน",
        },
        ["strict.scopeLabel"] = new() { ["en"] = "Covers", ["th"] = "ครอบคลุม" },
        ["strict.scopeTip"] = new() { ["en"] = "Which hard-to-undo commands wait for a code", ["th"] = "คำสั่งย้อนกลับยากแบบไหนที่ต้องรอรหัส" },
        ["strict.scope.all"] = new() { ["en"] = "Everything: this PC, remote ssh and git", ["th"] = "ทั้งหมด: เครื่องนี้ ssh ไปเครื่องอื่น และ git" },
        ["strict.scope.remote-git"] = new() { ["en"] = "Remote ssh and git force / reset only", ["th"] = "เฉพาะ ssh ไปเครื่องอื่น และ git force / reset" },
        ["strict.scope.remote"] = new() { ["en"] = "Remote ssh only", ["th"] = "เฉพาะ ssh ไปเครื่องอื่น" },

        // ─── What kind of change ───
        ["strict.kind.git-force"] = new() { ["en"] = "Force push (overwrites the remote's history)", ["th"] = "Force push (เขียนทับประวัติบน remote)" },
        ["strict.kind.git-loss"] = new() { ["en"] = "Git that throws work away", ["th"] = "คำสั่ง git ที่ทิ้งงานที่ทำไว้" },
        ["strict.kind.wipe"] = new() { ["en"] = "Wipes a drive, the root, a home folder, backups or a database", ["th"] = "ล้างทั้งไดรฟ์ root โฟลเดอร์ผู้ใช้ แบ็กอัป หรือฐานข้อมูล" },
        ["strict.kind.format"] = new() { ["en"] = "Formats or wipes a disk", ["th"] = "ฟอร์แมตหรือล้างดิสก์" },
        ["strict.kind.database"] = new() { ["en"] = "Destroys database rows or tables", ["th"] = "ลบข้อมูลหรือตารางในฐานข้อมูล" },
        ["strict.kind.backup"] = new() { ["en"] = "Deletes Windows backups or boot settings", ["th"] = "ลบแบ็กอัปของ Windows หรือการตั้งค่าบูต" },
        ["strict.kind.prune"] = new() { ["en"] = "Prunes or removes containers, images, volumes or packages", ["th"] = "ล้างหรือลบ container, image, volume หรือแพ็กเกจ" },
        ["strict.kind.registry"] = new() { ["en"] = "Changes the Windows registry", ["th"] = "แก้ไข registry ของ Windows" },
        ["strict.kind.service"] = new() { ["en"] = "Stops or changes services, tasks, power or permissions", ["th"] = "หยุดหรือแก้ service, งานตั้งเวลา, การปิดเครื่อง หรือสิทธิ์ไฟล์" },
        ["strict.kind.deploy"] = new() { ["en"] = "Deploys or publishes (reaches production)", ["th"] = "Deploy หรือ publish (ถึงระบบจริง)" },
        ["strict.kind.delete"] = new() { ["en"] = "Deletes files or folders", ["th"] = "ลบไฟล์หรือโฟลเดอร์" },

        // ─── The card ───
        ["strict.card.title"] = new() { ["en"] = "🔐 Strict approval — this is hard to undo", ["th"] = "🔐 อนุมัติแบบเข้ม — คำสั่งนี้ย้อนกลับยาก" },
        ["strict.card.body"] = new()
        {
            ["en"] = "It runs only if you type the code below (or \"yes\") here, or approve it from your own terminal. One approval = this exact command, once.",
            ["th"] = "จะรันก็ต่อเมื่อคุณพิมพ์รหัสด้านล่าง (หรือ \"yes\") ในช่องนี้ หรืออนุมัติจากเทอร์มินัลของคุณเอง อนุมัติหนึ่งครั้ง = คำสั่งนี้เป๊ะ ๆ หนึ่งครั้ง",
        },
        ["strict.card.where"] = new() { ["en"] = "Runs on", ["th"] = "รันที่" },
        ["strict.card.kind"] = new() { ["en"] = "Change", ["th"] = "การเปลี่ยนแปลง" },
        ["strict.card.tool"] = new() { ["en"] = "Asked by", ["th"] = "ขอโดย" },
        ["strict.card.command"] = new() { ["en"] = "The exact command", ["th"] = "คำสั่งจริงทั้งหมด" },
        ["strict.card.modelMustSay"] = new()
        {
            ["en"] = "The AI must say in the chat above what this deletes or changes and what it will NOT touch. If it did not, deny and ask it to.",
            ["th"] = "AI ต้องบอกในแชทด้านบนว่าคำสั่งนี้ลบหรือเปลี่ยนอะไร และอะไรที่จะไม่แตะ ถ้ายังไม่ได้บอก ให้กดปฏิเสธแล้วสั่งให้อธิบายก่อน",
        },
        ["strict.card.code"] = new() { ["en"] = "Code", ["th"] = "รหัส" },
        ["strict.card.expires"] = new() { ["en"] = "valid until", ["th"] = "ใช้ได้ถึง" },
        ["strict.card.inputTip"] = new() { ["en"] = "Type the code (or yes) and press Enter", ["th"] = "พิมพ์รหัส (หรือ yes) แล้วกด Enter" },
        ["strict.card.approve"] = new() { ["en"] = "Approve this command", ["th"] = "อนุมัติคำสั่งนี้" },
        ["strict.card.copyTerminal"] = new() { ["en"] = "Copy terminal command", ["th"] = "คัดลอกคำสั่งสำหรับเทอร์มินัล" },
        ["strict.byTerminal"] = new() { ["en"] = "Approved from your terminal.", ["th"] = "อนุมัติจากเทอร์มินัลของคุณแล้ว" },

        ["strict.answer.Approved"] = new() { ["en"] = "", ["th"] = "" },
        ["strict.answer.WrongCode"] = new() { ["en"] = "That is not the code — {0} tries left.", ["th"] = "รหัสไม่ถูก — เหลืออีก {0} ครั้ง" },
        ["strict.answer.Expired"] = new() { ["en"] = "The code expired — nothing ran. The AI has to ask again.", ["th"] = "รหัสหมดอายุแล้ว — ไม่มีอะไรถูกรัน AI ต้องขอใหม่" },
        ["strict.answer.AlreadyAnswered"] = new() { ["en"] = "This approval was already answered.", ["th"] = "การอนุมัตินี้ตอบไปแล้ว" },
        ["strict.answer.Locked"] = new() { ["en"] = "Too many wrong codes — refused, nothing ran.", ["th"] = "ใส่รหัสผิดหลายครั้งเกินไป — ปฏิเสธแล้ว ไม่มีอะไรถูกรัน" },
        ["strict.answer.NotFound"] = new() { ["en"] = "This approval is gone (CluadeX restarted?) — nothing ran.", ["th"] = "การอนุมัตินี้หายไปแล้ว (CluadeX เปิดใหม่?) — ไม่มีอะไรถูกรัน" },

        // ─── BrainX's own approval ───
        ["strict.brainx.title"] = new() { ["en"] = "🛡 BrainX is holding a remote command for your approval", ["th"] = "🛡 BrainX พักคำสั่งบนเซิร์ฟเวอร์ไว้รอคุณอนุมัติ" },
        ["strict.brainx.profile"] = new() { ["en"] = "Server profile:", ["th"] = "โปรไฟล์เซิร์ฟเวอร์:" },
        ["strict.brainx.body"] = new()
        {
            ["en"] = "Approve it in BrainX's own terminal (it shows the command and asks you to type yes). Then press \"I approved it\" and CluadeX sends the same command again — the AI never sees the approval id.",
            ["th"] = "อนุมัติในเทอร์มินัลของ BrainX เอง (มันจะแสดงคำสั่งแล้วให้พิมพ์ yes) จากนั้นกด \"อนุมัติแล้ว\" CluadeX จะส่งคำสั่งเดิมซ้ำให้ — AI ไม่เห็นรหัสอนุมัติเลย",
        },
        ["strict.brainx.open"] = new() { ["en"] = "Open approval terminal", ["th"] = "เปิดเทอร์มินัลอนุมัติ" },
        ["strict.brainx.copy"] = new() { ["en"] = "Copy approve command", ["th"] = "คัดลอกคำสั่งอนุมัติ" },
        ["strict.brainx.done"] = new() { ["en"] = "I approved it — run it", ["th"] = "อนุมัติแล้ว — รันเลย" },
    };
}
