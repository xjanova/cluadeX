namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the new chat page (an empty chat): the "what do you want to make?" templates, their
/// prompts and the project question asked before one is filled in. Every key needs both "en" and "th". Each prompt
/// carries one [bracketed] part — the view selects it, so the first thing typed replaces it.</summary>
internal static class StarterKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["starter.title"] = new() { ["en"] = "What do you want to make?", ["th"] = "วันนี้อยากทำอะไรดี?" },
        ["starter.subtitle"] = new()
        {
            ["en"] = "Pick a starter — it fills the box and you edit it before sending. Or just type.",
            ["th"] = "เลือกแบบที่ต้องการ ระบบจะเติมข้อความให้ แก้ได้ก่อนส่ง — หรือพิมพ์เองเลยก็ได้",
        },

        ["starter.app"] = new() { ["en"] = "Build an app", ["th"] = "สร้างแอป" },
        ["starter.app.hint"] = new() { ["en"] = "A desktop or mobile app, from idea to running", ["th"] = "แอปเดสก์ท็อปหรือมือถือ ตั้งแต่ไอเดียจนรันได้" },
        ["starter.app.prompt"] = new()
        {
            ["en"] = "Help me build an app: [what it does, who uses it, the main features]\nStart by proposing the tech stack and project structure for me to approve, then build it step by step.",
            ["th"] = "ช่วยสร้างแอป: [แอปนี้ทำอะไร ใครใช้ ฟีเจอร์หลักที่อยากได้]\nเริ่มจากเสนอเทคโนโลยีและโครงสร้างโปรเจกต์ให้ฉันเลือกก่อน แล้วค่อยลงมือทีละขั้น",
        },
        ["starter.web"] = new() { ["en"] = "Build a website", ["th"] = "สร้างเว็บ" },
        ["starter.web.hint"] = new() { ["en"] = "Pages that work on phones too, previewed in the browser", ["th"] = "เว็บที่ใช้บนมือถือได้ เปิดดูผลในเบราว์เซอร์" },
        ["starter.web.prompt"] = new()
        {
            ["en"] = "Help me build a website: [what it is about, which pages, the style I like]\nMake it work on phones and open it in the browser so I can see it.",
            ["th"] = "ช่วยสร้างเว็บไซต์: [เว็บเกี่ยวกับอะไร มีหน้าอะไรบ้าง สไตล์ที่ชอบ]\nทำให้ใช้บนมือถือได้ และเปิดในเบราว์เซอร์ให้ฉันดูผล",
        },
        ["starter.program"] = new() { ["en"] = "Write a program", ["th"] = "ทำโปรแกรม" },
        ["starter.program.hint"] = new() { ["en"] = "A tool or script that really runs", ["th"] = "เครื่องมือหรือสคริปต์ที่รันได้จริง" },
        ["starter.program.prompt"] = new()
        {
            ["en"] = "Help me write a program: [what it takes in, what it does, what it gives back]\nPick a fitting language, make it really run, and tell me how to run it.",
            ["th"] = "ช่วยเขียนโปรแกรม: [รับอะไรเข้าไป ทำอะไร ได้ผลลัพธ์อะไร]\nเลือกภาษาที่เหมาะ เขียนให้รันได้จริง และบอกวิธีรัน",
        },
        ["starter.game"] = new() { ["en"] = "Make a game", ["th"] = "สร้างเกม" },
        ["starter.game.hint"] = new() { ["en"] = "Start small and playable, then grow it", ["th"] = "เริ่มจากเวอร์ชันเล็กที่เล่นได้ แล้วค่อยเพิ่ม" },
        ["starter.game.prompt"] = new()
        {
            ["en"] = "Help me make a game: [the genre, how it plays, the look I want]\nStart with the smallest version that can be played, then add to it.",
            ["th"] = "ช่วยสร้างเกม: [แนวเกม วิธีเล่น หน้าตาที่อยากได้]\nเริ่มจากเวอร์ชันเล็กที่สุดที่เล่นได้ก่อน แล้วค่อยเพิ่มของ",
        },
        ["starter.design"] = new() { ["en"] = "Design a UI", ["th"] = "ออกแบบ UI" },
        ["starter.design.hint"] = new() { ["en"] = "A screen laid out, then built as real code", ["th"] = "วางเลย์เอาต์หน้าจอ แล้วสร้างเป็นโค้ดจริง" },
        ["starter.design.prompt"] = new()
        {
            ["en"] = "Help me design a UI: [which screen, what it is for, the colours and mood I want]\nPropose a layout first, then build it as real code I can look at.",
            ["th"] = "ช่วยออกแบบ UI: [หน้าจอไหน ใช้ทำอะไร โทนสีและอารมณ์ที่อยากได้]\nเสนอเลย์เอาต์ก่อน แล้วสร้างเป็นโค้ดจริงที่ฉันเปิดดูได้",
        },
        ["starter.bug"] = new() { ["en"] = "Fix a bug", ["th"] = "แก้บั๊ก" },
        ["starter.bug.hint"] = new() { ["en"] = "Find the real cause, fix it, prove it is gone", ["th"] = "หาสาเหตุจริง แก้ แล้วพิสูจน์ว่าหาย" },
        ["starter.bug.prompt"] = new()
        {
            ["en"] = "Help me fix a bug: [what goes wrong, what I do to make it happen, the error message if any]\nFind the real cause before changing anything, then prove it is fixed.",
            ["th"] = "ช่วยแก้บั๊ก: [อาการที่เจอ ทำอะไรแล้วเกิด ข้อความ error ถ้ามี]\nหาสาเหตุจริงก่อนแก้ แล้วพิสูจน์ว่าหายแล้ว",
        },
        ["starter.analyze"] = new() { ["en"] = "Analyze the project", ["th"] = "วิเคราะห์โปรเจกต์" },
        ["starter.analyze.hint"] = new() { ["en"] = "Architecture, risks and what to improve first", ["th"] = "โครงสร้าง จุดเสี่ยง และสิ่งที่ควรปรับก่อน" },
        ["starter.analyze.prompt"] = new()
        {
            ["en"] = "Analyze this project: [anything in particular — structure, risky parts, technical debt]\nSum up the architecture, the important parts, and what to improve first.",
            ["th"] = "วิเคราะห์โปรเจกต์นี้: [อยากรู้เรื่องไหนเป็นพิเศษ — โครงสร้าง จุดเสี่ยง หนี้เทคนิค]\nสรุปสถาปัตยกรรม ส่วนสำคัญ และสิ่งที่ควรปรับปรุงก่อน",
        },
        ["starter.comment"] = new() { ["en"] = "Comment the code", ["th"] = "คอมเมนต์โค้ด" },
        ["starter.comment.hint"] = new() { ["en"] = "Explain what each part does, behaviour unchanged", ["th"] = "อธิบายว่าแต่ละส่วนทำอะไร โดยไม่เปลี่ยนการทำงาน" },
        ["starter.comment.prompt"] = new()
        {
            ["en"] = "Add comments that explain the code: [which files or folders]\nSay what each part does and why, without changing how the code behaves.",
            ["th"] = "เพิ่มคอมเมนต์อธิบายโค้ด: [ไฟล์หรือโฟลเดอร์ที่ต้องการ]\nอธิบายว่าแต่ละส่วนทำอะไรและทำไม โดยไม่เปลี่ยนการทำงานของโค้ด",
        },
        ["starter.test"] = new() { ["en"] = "Write tests", ["th"] = "ทดสอบ" },
        ["starter.test.hint"] = new() { ["en"] = "Normal, wrong and edge cases — and run them", ["th"] = "กรณีปกติ กรณีผิด กรณีขอบ แล้วรันให้ผ่าน" },
        ["starter.test.prompt"] = new()
        {
            ["en"] = "Write tests for: [which part of the code, or the feature I want to be sure of]\nCover the normal case, wrong input and the edges, then run them until they pass.",
            ["th"] = "เขียนเทสต์ให้: [โค้ดส่วนไหน หรือฟีเจอร์ที่อยากมั่นใจ]\nครอบคลุมกรณีปกติ ข้อมูลผิด และกรณีขอบ แล้วรันจนผ่าน",
        },
        ["starter.research"] = new() { ["en"] = "Research", ["th"] = "ค้นคว้า" },
        ["starter.research.hint"] = new() { ["en"] = "Compare the options and recommend one", ["th"] = "เทียบตัวเลือก แล้วสรุปคำแนะนำ" },
        ["starter.research.prompt"] = new()
        {
            ["en"] = "Research this for me: [the topic or question]\nCompare the options with their pros and cons, then recommend one and cite your sources.",
            ["th"] = "ช่วยค้นคว้า: [หัวข้อหรือคำถามที่อยากรู้]\nเทียบตัวเลือก ข้อดีข้อเสีย แล้วสรุปคำแนะนำพร้อมแหล่งอ้างอิง",
        },
        ["starter.deploy"] = new() { ["en"] = "Deploy", ["th"] = "Deploy" },
        ["starter.deploy.hint"] = new() { ["en"] = "Ship it — asks before anything irreversible", ["th"] = "ขึ้นระบบจริง — ถามก่อนทำสิ่งที่ย้อนไม่ได้" },
        ["starter.deploy.prompt"] = new()
        {
            ["en"] = "Help me deploy: [where to — a server, a host, an app store]\nCheck what is needed, go step by step, and ask me before anything that cannot be undone.",
            ["th"] = "ช่วย deploy: [ขึ้นที่ไหน — เซิร์ฟเวอร์ โฮสติ้ง หรือสโตร์]\nเช็กสิ่งที่ต้องเตรียม ทำทีละขั้น และถามฉันก่อนทำอะไรที่ย้อนกลับไม่ได้",
        },
        ["starter.explain"] = new() { ["en"] = "Explain code", ["th"] = "อธิบายโค้ด" },
        ["starter.explain.hint"] = new() { ["en"] = "Step by step, with examples", ["th"] = "ทีละขั้น เข้าใจง่าย พร้อมตัวอย่าง" },
        ["starter.explain.prompt"] = new()
        {
            ["en"] = "Explain this code to me: [the file, function or part I want to understand]\nKeep it simple, step by step, with examples.",
            ["th"] = "อธิบายโค้ดให้หน่อย: [ไฟล์ ฟังก์ชัน หรือส่วนที่อยากเข้าใจ]\nอธิบายแบบเข้าใจง่าย ทีละขั้น พร้อมตัวอย่าง",
        },

        // The project question: a new chat has no folder, and a starter's work happens in one.
        ["starter.project.title"] = new() { ["en"] = "{0} — in which project?", ["th"] = "{0} — ทำในโปรเจกต์ไหน?" },
        ["starter.project.recent"] = new() { ["en"] = "Recent projects", ["th"] = "โปรเจกต์ล่าสุด" },
        ["starter.project.current"] = new() { ["en"] = "open now", ["th"] = "เปิดอยู่" },
        ["starter.project.empty"] = new()
        {
            ["en"] = "No recent projects yet — choose a folder, or make a new one in the picker.",
            ["th"] = "ยังไม่มีโปรเจกต์ล่าสุด — เลือกโฟลเดอร์ หรือสร้างโฟลเดอร์ใหม่ในหน้าต่างเลือกได้เลย",
        },
        ["starter.project.browse"] = new() { ["en"] = "Choose or create a folder…", ["th"] = "เลือกหรือสร้างโฟลเดอร์…" },
        ["starter.project.none"] = new() { ["en"] = "No project — just talk", ["th"] = "ไม่ใช้โปรเจกต์ — คุยอย่างเดียว" },
        ["starter.project.noneTip"] = new()
        {
            ["en"] = "The agent gets no file or shell tools without a project folder.",
            ["th"] = "ถ้าไม่มีโฟลเดอร์โปรเจกต์ เอเจนต์จะไม่มีเครื่องมือจัดการไฟล์และคำสั่ง",
        },
        ["starter.project.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["starter.project.replaceWarning"] = new()
        {
            ["en"] = "What you typed in the box will be replaced by this starter.",
            ["th"] = "ข้อความที่พิมพ์ไว้ในช่องจะถูกแทนที่ด้วยข้อความของแบบนี้",
        },
        ["starter.project.gone"] = new()
        {
            ["en"] = "The folder {0} is gone — pick another project.",
            ["th"] = "ไม่พบโฟลเดอร์ {0} แล้ว — เลือกโปรเจกต์อื่น",
        },
        ["starter.filled"] = new()
        {
            ["en"] = "Starter filled in — edit the part in [brackets], then press Enter to send.",
            ["th"] = "เติมข้อความให้แล้ว — แก้ส่วนใน [วงเล็บ] แล้วกด Enter เพื่อส่ง",
        },
    };
}
