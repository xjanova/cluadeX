namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Settings page's layout (2026-11-10 clean-up, ViewModels/SettingsViewModel.Layout.cs):
/// the grouped provider picker, the per-provider fields of the connection card, the sampling notes, the dispatch card's
/// advanced section and the Voice category. Every key needs both "en" and "th". Changed texts of older keys are edited
/// in LocalizationService itself (a key defined there wins over these files).</summary>
internal static class SettingsLayoutKeys
{
    private static Dictionary<string, string> E(string en, string th) => new() { ["en"] = en, ["th"] = th };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── the connection card ──
        ["settings.inUse"] = E("Answering now:", "ใช้ตอบอยู่:"),
        ["settings.provGroup.cloud"] = E("Cloud APIs (with a key)", "Cloud API (ใช้คีย์)"),
        ["settings.provGroup.cli"] = E("Installed CLIs (your sign-in, no key)", "โปรแกรม CLI ที่ติดตั้งไว้ (ใช้การล็อกอินเดิม ไม่ต้องใช้คีย์)"),
        ["settings.provGroup.compatible"] = E("OpenAI-compatible services", "บริการแบบ OpenAI-compatible"),
        ["settings.provGroup.local"] = E("On this PC", "รันบนเครื่องนี้"),
        ["settings.provGroup.other"] = E("Other", "อื่นๆ"),

        ["settings.apiKeyOptional"] = E("API key (optional)", "API key (ไม่บังคับ)"),
        ["settings.apiKeyOptionalDesc"] = E("Only when the server was started with a key — otherwise leave it blank.",
            "ใส่เฉพาะเมื่อเปิดเซิร์ฟเวอร์แบบตั้งคีย์ไว้ · ปกติเว้นว่างได้"),
        ["settings.baseUrlRequired"] = E("Base URL (required)", "Base URL (จำเป็น)"),
        ["settings.baseUrlRequiredDesc"] = E("The service's own address, usually ending in /v1 (Azure: your resource's URL).",
            "ที่อยู่ของบริการ ส่วนใหญ่ลงท้ายด้วย /v1 (Azure: URL ของ resource ของคุณ)"),
        ["settings.baseUrlServer"] = E("Server address", "ที่อยู่เซิร์ฟเวอร์"),
        ["settings.baseUrlServerDesc"] = E("Leave blank for the usual address on this PC; type another machine's address to use that one.",
            "เว้นว่าง = ที่อยู่ปกติบนเครื่องนี้ · ใส่ที่อยู่ของเครื่องอื่นถ้าจะใช้เครื่องนั้น"),
        ["settings.baseUrlOverride"] = E("Use another address (proxy or gateway)", "ใช้ที่อยู่อื่น (proxy หรือ gateway)"),

        ["settings.provNote.cli"] = E("Uses the CLI's own sign-in on this PC — no key. The model below is its default; the chat's model box can switch it.",
            "ใช้การล็อกอินของโปรแกรม CLI บนเครื่องนี้ ไม่ต้องใส่คีย์ · โมเดลด้านล่างคือค่าเริ่มต้น สลับได้ที่ช่องโมเดลในหน้าแชต"),
        ["settings.provNote.brainx"] = E("Sends the request to whoever is in the BrainX cowork room. There is nothing to set here.",
            "ส่งคำขอไปให้ผู้ที่อยู่ในห้อง cowork ของ BrainX ตอบ · ไม่มีอะไรต้องตั้งค่าที่นี่"),
        ["settings.provNote.colibri"] = E("The Colibri plugin fills in its address and model while it runs — install and start it on the Plugins page.",
            "ปลั๊กอิน Colibri ใส่ที่อยู่และโมเดลให้เองตอนเปิดใช้งาน · ติดตั้งและเปิดได้ที่หน้า Plugins"),
        ["settings.provNote.gguf"] = E("A GGUF file on this PC: pick, download and load it on the Models page. Its GPU and context options are under Local models.",
            "ไฟล์ GGUF บนเครื่องนี้: เลือก ดาวน์โหลด และโหลดได้ที่หน้า Models · ตัวเลือก GPU และขนาดบริบทอยู่ในหมวด โมเดลในเครื่อง"),
        ["settings.openModelsPage"] = E("Open the Models page", "เปิดหน้า Models"),
        ["settings.openPluginsPage"] = E("Open the Plugins page", "เปิดหน้า Plugins"),

        // ── Model & output ──
        ["settings.samplingNoteBoth"] = E("{0} does not read Temperature or Top P — they show again when the chat uses a provider that does.",
            "{0} ไม่ใช้ค่า Temperature และ Top P — จะแสดงอีกครั้งเมื่อแชตใช้ผู้ให้บริการที่ใช้ค่านี้"),
        ["settings.samplingNoteTopP"] = E("{0} does not read Top P (only Gemini, Ollama and local GGUF models do).",
            "{0} ไม่ใช้ค่า Top P (ใช้เฉพาะ Gemini, Ollama และโมเดล GGUF ในเครื่อง)"),

        // ── Agent ▸ dispatch ──
        ["settings.dispatchAdvanced"] = E("Advanced: a model per kind of work", "ขั้นสูง: กำหนดโมเดลแยกตามประเภทงาน"),
        ["settings.dispatchAdvancedDesc"] = E(
            "With auto model on, the tier boxes above come first; these fill in where a tier box is blank (explore for read-only work, code / review / hard fix for the deep tier). With auto model off, these are the only picks. Blank = the run's own model.",
            "เมื่อเปิดโหมดเลือกโมเดลอัตโนมัติ ช่องตามระดับด้านบนมาก่อน ช่องเหล่านี้ใช้เมื่อช่องระดับว่าง (สำรวจ = งานอ่านอย่างเดียว, เขียนโค้ด/รีวิว/แก้ยาก = ระดับ deep) · เมื่อปิดโหมดอัตโนมัติ ใช้ช่องเหล่านี้อย่างเดียว · ว่าง = ใช้โมเดลของงานหลัก"),

        // ── Memory ──
        ["settings.card.memory"] = E("Memory", "ความจำ"),

        // ── Local models ▸ sampling ──
        ["settings.topK"] = E("Top-K · llama-server / Ollama only", "Top-K · llama-server / Ollama เท่านั้น"),
        ["settings.minP"] = E("Min-P · llama-server / Ollama only", "Min-P · llama-server / Ollama เท่านั้น"),

        // ── Voice ──
        ["settings.cat.voice"] = E("Voice", "เสียง"),
        ["settings.catDesc.voice"] = E("Reading replies aloud, who reads them, and the live voice call.",
            "อ่านคำตอบออกเสียง เลือกเสียงที่อ่าน และการคุยด้วยเสียงสด"),
    };
}
