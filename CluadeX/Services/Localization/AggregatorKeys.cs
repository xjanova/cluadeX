namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the OpenAI-compatible services (CompatiblePresets): each one's "how to get a key"
/// guide on the Settings provider card ("agg.{type}.steps" / ".free"), and the client's own errors. Every key needs both
/// "en" and "th". Free-tier notes say only what the service's own pages said on 2026-11-02, and point at the page that
/// has the current offer.</summary>
internal static class AggregatorKeys
{
    private static Dictionary<string, string> E(string en, string th) => new() { ["en"] = en, ["th"] = th };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── Settings card ──
        ["settings.agg.guide"] = E("How to get an API key", "วิธีขอ API key"),
        ["settings.agg.open"] = E("Open the key page", "เปิดหน้าสร้างคีย์"),
        ["settings.agg.free"] = E("Free use / credit", "ใช้ฟรี / เครดิต"),
        ["settings.agg.baseUrlExample"] = E("Base URL example: {0}", "ตัวอย่าง Base URL: {0}"),
        ["settings.agg.noKeyNeeded"] = E("Runs on this PC — no API key needed.", "รันบนเครื่องนี้ — ไม่ต้องใช้ API key"),

        // ── client errors ──
        ["agg.err.baseUrl"] = E("{0} needs a Base URL — for example {1}.", "{0} ต้องใส่ Base URL — เช่น {1}"),
        ["agg.err.noModel"] = E("{0}: pick a model, or tick “Use custom model ID” and type one.",
            "{0}: เลือกโมเดล หรือติ๊ก “ใช้ Model ID กำหนดเอง” แล้วพิมพ์ชื่อโมเดล"),
        ["agg.err.ownConsole"] = new() { ["en"] = "the service's own console", ["th"] = "หน้าจัดการคีย์ของบริการนั้น" },
        ["agg.err.localDown"] = E("{0} doesn't answer at {1} — start its server, then press Test Connection.",
            "{0} ไม่ตอบที่ {1} — เปิดเซิร์ฟเวอร์ของมันก่อน แล้วกด ทดสอบการเชื่อมต่อ"),

        // ── guides ──
        ["agg.openrouter.steps"] = E(
            "1. Open openrouter.ai and sign in (Google, GitHub or email).\n2. Settings ▸ Keys ▸ Create Key.\n3. Copy the key (sk-or-…), paste it above, press Test Connection.\n4. Test Connection lists every model; pick one with tool support for coding.",
            "1. เปิด openrouter.ai แล้วล็อกอิน (Google, GitHub หรืออีเมล)\n2. ไปที่ Settings ▸ Keys ▸ Create Key\n3. ก๊อปคีย์ (sk-or-…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ\n4. ระบบจะดึงรายชื่อโมเดลทั้งหมดมาให้ เลือกตัวที่รองรับ tools สำหรับงานโค้ด"),
        ["agg.openrouter.free"] = E(
            "Free models (ids ending in :free, or the openrouter/free router) need no credit: 20 requests/min and 50 a day, 1,000 a day once you have bought $10 of credit. Other models are pay-per-token from prepaid credit.",
            "โมเดลฟรี (ชื่อลงท้าย :free หรือ openrouter/free) ใช้ได้โดยไม่ต้องเติมเงิน: 20 ครั้ง/นาที และ 50 ครั้ง/วัน (1,000 ครั้ง/วัน เมื่อเคยเติมเครดิตครบ $10) โมเดลอื่นคิดตามจำนวน token จากเครดิตที่เติมไว้"),

        ["agg.groq.steps"] = E(
            "1. Open console.groq.com and sign in.\n2. API Keys ▸ Create API Key.\n3. Copy the key (gsk_…) — it is shown once — paste it above, press Test Connection.",
            "1. เปิด console.groq.com แล้วล็อกอิน\n2. ไปที่ API Keys ▸ Create API Key\n3. ก๊อปคีย์ (gsk_…) ซึ่งจะแสดงครั้งเดียว มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.groq.free"] = E(
            "A free plan with rate limits (your numbers are under Settings ▸ Limits); no card needed to start. Very fast, good for quick agent steps.",
            "มีแพ็กเกจฟรีแบบจำกัดอัตรา (ดูตัวเลขของคุณที่ Settings ▸ Limits) ไม่ต้องใช้บัตรเพื่อเริ่ม เร็วมาก เหมาะกับงาน agent ทีละขั้น"),

        ["agg.deepseek.steps"] = E(
            "1. Open platform.deepseek.com and sign up.\n2. Top up a balance (pay as you go).\n3. API Keys ▸ Create new API key, copy it (sk-…), paste it above, press Test Connection.",
            "1. เปิด platform.deepseek.com แล้วสมัคร\n2. เติมเงิน (จ่ายตามการใช้งาน)\n3. ไปที่ API Keys ▸ Create new API key ก๊อปคีย์ (sk-…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.deepseek.free"] = E(
            "No free tier — pay as you go from your balance, among the cheapest strong coding models. A key with no balance is refused (402).",
            "ไม่มีแบบฟรี — จ่ายตามใช้จากยอดเงินที่เติม เป็นโมเดลโค้ดเก่งที่ถูกที่สุดกลุ่มหนึ่ง คีย์ที่ไม่มียอดเงินจะถูกปฏิเสธ (402)"),

        ["agg.mistral.steps"] = E(
            "1. Open console.mistral.ai and sign in.\n2. Choose a plan under Billing (keys only work once a plan is active).\n3. API Keys ▸ Create new key, copy it, paste it above, press Test Connection.",
            "1. เปิด console.mistral.ai แล้วล็อกอิน\n2. เลือกแพ็กเกจที่ Billing (คีย์จะใช้ได้เมื่อเปิดแพ็กเกจแล้ว)\n3. ไปที่ API Keys ▸ Create new key ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.mistral.free"] = E(
            "Mistral has offered a free “Experiment” plan with low rate limits — check the billing page for the current offer.",
            "Mistral เคยมีแพ็กเกจ “Experiment” ใช้ฟรีแบบจำกัดอัตรา — ดูข้อเสนอปัจจุบันที่หน้า Billing"),

        ["agg.xai.steps"] = E(
            "1. Open console.x.ai and sign in (X account or email).\n2. Add credit under Billing.\n3. API Keys ▸ Create API Key, copy it (xai-…), paste it above, press Test Connection.",
            "1. เปิด console.x.ai แล้วล็อกอิน (บัญชี X หรืออีเมล)\n2. เติมเครดิตที่ Billing\n3. ไปที่ API Keys ▸ Create API Key ก๊อปคีย์ (xai-…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.xai.free"] = E(
            "Pay as you go from prepaid credit; your rate limits rise with what you have spent.",
            "จ่ายตามใช้จากเครดิตที่เติมไว้ ขีดจำกัดการใช้งานจะเพิ่มขึ้นตามยอดที่เคยใช้"),

        ["agg.together.steps"] = E(
            "1. Open api.together.ai and sign up.\n2. Settings ▸ API Keys, copy your key.\n3. Paste it above and press Test Connection — the list shows the serverless models.",
            "1. เปิด api.together.ai แล้วสมัคร\n2. ไปที่ Settings ▸ API Keys ก๊อปคีย์ของคุณ\n3. วางด้านบนแล้วกด ทดสอบการเชื่อมต่อ — จะแสดงรายชื่อโมเดลแบบ serverless"),
        ["agg.together.free"] = E(
            "Pay per token; prices show in the model list. Check the billing page for any starter credit.",
            "คิดตามจำนวน token (ราคาแสดงในรายชื่อโมเดล) ดูเครดิตเริ่มต้น (ถ้ามี) ที่หน้า Billing"),

        ["agg.fireworks.steps"] = E(
            "1. Open fireworks.ai and sign in.\n2. Account ▸ API Keys ▸ Create API Key.\n3. Copy it (fw_…), paste it above, press Test Connection. Model ids look like accounts/fireworks/models/<name>.",
            "1. เปิด fireworks.ai แล้วล็อกอิน\n2. ไปที่ Account ▸ API Keys ▸ Create API Key\n3. ก๊อปคีย์ (fw_…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ ชื่อโมเดลจะเป็นแบบ accounts/fireworks/models/<ชื่อ>"),
        ["agg.fireworks.free"] = E(
            "Pay per token. Check the billing page for any starter credit.",
            "คิดตามจำนวน token ดูเครดิตเริ่มต้น (ถ้ามี) ที่หน้า Billing"),

        ["agg.cerebras.steps"] = E(
            "1. Open cloud.cerebras.ai and sign in.\n2. API Keys — copy your key (csk-…).\n3. Paste it above and press Test Connection.",
            "1. เปิด cloud.cerebras.ai แล้วล็อกอิน\n2. ไปที่ API Keys ก๊อปคีย์ของคุณ (csk-…)\n3. วางด้านบนแล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.cerebras.free"] = E(
            "Free trial on gpt-oss-120b and qwen-3.8-27b: 5 requests/min and 1M tokens a day. Adding a verified payment method gives $5 of credit that expires after 30 days.",
            "ทดลองฟรีกับ gpt-oss-120b และ qwen-3.8-27b: 5 ครั้ง/นาที และ 1 ล้าน token/วัน ผูกช่องทางชำระเงินที่ยืนยันแล้วจะได้เครดิต $5 ใช้ได้ 30 วัน"),

        ["agg.deepinfra.steps"] = E(
            "1. Open deepinfra.com and sign in (GitHub or email).\n2. Dashboard ▸ API Keys ▸ New API Key.\n3. Copy it, paste it above, press Test Connection.",
            "1. เปิด deepinfra.com แล้วล็อกอิน (GitHub หรืออีเมล)\n2. ไปที่ Dashboard ▸ API Keys ▸ New API Key\n3. ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.deepinfra.free"] = E(
            "Pay per token from a prepaid balance; low prices on open models. Replies are capped at 16,384 tokens.",
            "คิดตามจำนวน token จากยอดเงินที่เติม ราคาโมเดลเปิดถูก คำตอบยาวได้สูงสุด 16,384 token"),

        ["agg.huggingface.steps"] = E(
            "1. Sign in at huggingface.co.\n2. The key page opens a fine-grained token with “Make calls to Inference Providers” already ticked — give it a name and press Create token.\n3. Copy it (hf_…), paste it above, press Test Connection.\nA model id may end in :fastest, :cheapest or :<provider> (e.g. openai/gpt-oss-120b:groq).",
            "1. ล็อกอินที่ huggingface.co\n2. ลิงก์จะเปิดหน้าสร้าง token แบบ fine-grained ที่ติ๊ก “Make calls to Inference Providers” ไว้แล้ว — ตั้งชื่อแล้วกด Create token\n3. ก๊อป token (hf_…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ\nชื่อโมเดลต่อท้ายได้ด้วย :fastest, :cheapest หรือ :<ผู้ให้บริการ> (เช่น openai/gpt-oss-120b:groq)"),
        ["agg.huggingface.free"] = E(
            "Free accounts get no inference credit — buy credit, or PRO includes $2 a month. You pay each provider's own price with no markup.",
            "บัญชีฟรีไม่มีเครดิตสำหรับเรียกโมเดล — ต้องซื้อเครดิต หรือสมัคร PRO ซึ่งมีให้ $2 ต่อเดือน ราคาเท่ากับของผู้ให้บริการแต่ละเจ้า ไม่บวกเพิ่ม"),

        ["agg.novita.steps"] = E(
            "1. Open novita.ai and sign in.\n2. Settings ▸ Key Management ▸ Add New Key.\n3. Copy it, paste it above, press Test Connection.",
            "1. เปิด novita.ai แล้วล็อกอิน\n2. ไปที่ Settings ▸ Key Management ▸ Add New Key\n3. ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.novita.free"] = E(
            "Pay per token. Check the billing page for any signup credit.",
            "คิดตามจำนวน token ดูเครดิตตอนสมัคร (ถ้ามี) ที่หน้า Billing"),

        ["agg.sambanova.steps"] = E(
            "1. Open cloud.sambanova.ai and sign in.\n2. APIs ▸ Create API Key.\n3. Copy it, paste it above, press Test Connection.",
            "1. เปิด cloud.sambanova.ai แล้วล็อกอิน\n2. ไปที่ APIs ▸ Create API Key\n3. ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.sambanova.free"] = E(
            "SambaNova has offered a rate-limited free tier — the plans page has the current offer.",
            "SambaNova เคยมีแบบใช้ฟรีแบบจำกัดอัตรา — ดูข้อเสนอปัจจุบันที่หน้า Plans"),

        ["agg.nebius.steps"] = E(
            "1. Open tokenfactory.nebius.com and sign in.\n2. Project ▸ API keys ▸ Create API key.\n3. Copy it, paste it above, press Test Connection.",
            "1. เปิด tokenfactory.nebius.com แล้วล็อกอิน\n2. ไปที่ Project ▸ API keys ▸ Create API key\n3. ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.nebius.free"] = E(
            "Pay per token. Check the console for any trial credit.",
            "คิดตามจำนวน token ดูเครดิตทดลอง (ถ้ามี) ในคอนโซล"),

        ["agg.qwen.steps"] = E(
            "1. Sign in to Alibaba Cloud (international) and open Model Studio in the Singapore region.\n2. API Key ▸ Create API Key.\n3. Copy it (sk-…), paste it above, press Test Connection.\nThe key must be from the international (Singapore) region — a China-region key is refused here (401).",
            "1. ล็อกอิน Alibaba Cloud (international) แล้วเปิด Model Studio ภูมิภาค Singapore\n2. ไปที่ API Key ▸ Create API Key\n3. ก๊อปคีย์ (sk-…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ\nคีย์ต้องมาจากภูมิภาค international (Singapore) — คีย์ภูมิภาคจีนจะถูกปฏิเสธ (401)"),
        ["agg.qwen.free"] = E(
            "Pay per token. New Model Studio accounts have received a free token quota per model — the console shows yours.",
            "คิดตามจำนวน token บัญชี Model Studio ใหม่เคยได้โควตา token ฟรีต่อโมเดล — ดูของคุณในคอนโซล"),

        ["agg.moonshot.steps"] = E(
            "1. Open platform.kimi.ai (Moonshot's international platform) and sign in.\n2. Console ▸ API Keys ▸ Create.\n3. Copy it (sk-…), paste it above, press Test Connection.",
            "1. เปิด platform.kimi.ai (แพลตฟอร์มสากลของ Moonshot) แล้วล็อกอิน\n2. ไปที่ Console ▸ API Keys ▸ Create\n3. ก๊อปคีย์ (sk-…) มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.moonshot.free"] = E(
            "Pay as you go from a recharged balance.",
            "จ่ายตามใช้จากยอดเงินที่เติม"),

        ["agg.zhipu.steps"] = E(
            "1. Open z.ai and sign in.\n2. API Keys ▸ Add new API key.\n3. Copy it, paste it above, press Test Connection.",
            "1. เปิด z.ai แล้วล็อกอิน\n2. ไปที่ API Keys ▸ Add new API key\n3. ก๊อปคีย์มาวางด้านบน แล้วกด ทดสอบการเชื่อมต่อ"),
        ["agg.zhipu.free"] = E(
            "Pay per token. A “GLM Coding Plan” key is a different key for a different address and does not work here.",
            "คิดตามจำนวน token คีย์ของ “GLM Coding Plan” เป็นคนละชนิด ใช้คนละที่อยู่ ใช้กับตรงนี้ไม่ได้"),

        ["agg.azureopenai.steps"] = E(
            "1. In the Azure portal open your Azure OpenAI (Foundry) resource ▸ Keys and Endpoint.\n2. Copy KEY 1 and paste it above.\n3. Base URL: https://<resource>.openai.azure.com/openai/v1\n4. Model: your deployment name — tick “Use custom model ID” and type it. Then Test Connection.",
            "1. ใน Azure portal เปิด resource Azure OpenAI (Foundry) ของคุณ ▸ Keys and Endpoint\n2. ก๊อป KEY 1 มาวางด้านบน\n3. Base URL: https://<resource>.openai.azure.com/openai/v1\n4. โมเดล: ชื่อ deployment ของคุณ — ติ๊ก “ใช้ Model ID กำหนดเอง” แล้วพิมพ์ จากนั้นกด ทดสอบการเชื่อมต่อ"),
        ["agg.azureopenai.free"] = E(
            "Billed to your Azure subscription. Test Connection sends one tiny request (there is no deployment list to read).",
            "เรียกเก็บผ่าน subscription Azure ของคุณ การทดสอบการเชื่อมต่อจะส่งคำขอเล็ก ๆ หนึ่งครั้ง (ไม่มีรายชื่อ deployment ให้อ่าน)"),

        ["agg.lmstudio.steps"] = E(
            "1. Install LM Studio and download a model (one with tool support, e.g. Qwen).\n2. Developer tab ▸ load the model ▸ Start Server (port 1234).\n3. Press Test Connection — the loaded models appear in the list.",
            "1. ติดตั้ง LM Studio แล้วดาวน์โหลดโมเดล (เลือกตัวที่รองรับ tools เช่น Qwen)\n2. แท็บ Developer ▸ โหลดโมเดล ▸ Start Server (พอร์ต 1234)\n3. กด ทดสอบการเชื่อมต่อ — โมเดลที่โหลดไว้จะขึ้นในรายการ"),
        ["agg.lmstudio.free"] = E(
            "Free — it runs on this PC's own hardware. No key unless you turned on “Require authentication”.",
            "ฟรี — รันด้วยฮาร์ดแวร์ของเครื่องนี้ ไม่ต้องใช้คีย์ เว้นแต่เปิด “Require authentication” ไว้"),

        ["agg.colibri.steps"] = E(
            "1. Plugins page ▸ On-PC AI · Colibri ▸ Install Colibri (needs Python 3).\n2. Models this PC can run ▸ pick one ▸ Download and set up.\n3. Start — the Base URL and model are filled in by themselves once she is ready.",
            "1. หน้า Plugins ▸ AI ในเครื่อง · น้อง Colibri ▸ ติดตั้ง Colibri (ต้องมี Python 3)\n2. ดูโมเดลที่เครื่องนี้รันได้ ▸ เลือก ▸ ดาวน์โหลดและตั้งค่า\n3. กด เปิดน้อง — Base URL และโมเดลจะถูกกรอกให้เองเมื่อพร้อม"),
        ["agg.colibri.free"] = E(
            "Free — big mixture-of-experts models on this PC: the core in RAM, the experts read from the SSD, the busiest on the GPU.",
            "ฟรี — โมเดล MoE ตัวใหญ่บนเครื่องนี้: ส่วนหลักอยู่ใน RAM ผู้เชี่ยวชาญอ่านจาก SSD ตัวที่ใช้บ่อยอยู่บนการ์ดจอ"),

        ["agg.vllm.steps"] = E(
            "1. Start the server: vllm serve <model> --enable-auto-tool-choice --tool-call-parser <parser>\n2. It listens on port 8000 by default; change the Base URL if not.\n3. Press Test Connection.",
            "1. เปิดเซิร์ฟเวอร์: vllm serve <model> --enable-auto-tool-choice --tool-call-parser <parser>\n2. ค่าเริ่มต้นฟังที่พอร์ต 8000 ถ้าไม่ใช่ให้แก้ Base URL\n3. กด ทดสอบการเชื่อมต่อ"),
        ["agg.vllm.free"] = E(
            "Free on your own GPU. Without --enable-auto-tool-choice CluadeX falls back to its text tool protocol.",
            "ฟรีบน GPU ของคุณเอง ถ้าไม่ใส่ --enable-auto-tool-choice CluadeX จะใช้โปรโตคอลเครื่องมือแบบข้อความแทน"),

        ["agg.openaicompatible.steps"] = E(
            "1. Base URL: the server's address up to /v1 (the part before /chat/completions). https only, or http to this PC.\n2. A key if the server needs one.\n3. Pick a model after Test Connection, or tick “Use custom model ID” and type it.",
            "1. Base URL: ที่อยู่ของเซิร์ฟเวอร์จนถึง /v1 (ส่วนก่อน /chat/completions) ต้องเป็น https หรือ http เฉพาะเครื่องนี้\n2. ใส่คีย์ถ้าเซิร์ฟเวอร์ต้องการ\n3. เลือกโมเดลหลังทดสอบการเชื่อมต่อ หรือติ๊ก “ใช้ Model ID กำหนดเอง” แล้วพิมพ์"),
        ["agg.openaicompatible.free"] = E(
            "Whatever the server charges — CluadeX counts the tokens and shows $0 when it doesn't know the price.",
            "แล้วแต่เซิร์ฟเวอร์คิดราคา — CluadeX นับ token ให้ และแสดง $0 เมื่อไม่รู้ราคา"),
    };
}
