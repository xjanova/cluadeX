namespace CluadeX.Services.Localization;

/// <summary>Localized strings for Settings › General › Text size and font (Views/Settings/TypographyPicker.xaml,
/// Services/TypographyService). Every key needs both "en" and "th".</summary>
internal static class TypographyKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["settings.card.typography"] = new() { ["en"] = "Text size and font", ["th"] = "ขนาดตัวอักษรและฟอนต์" },

        ["typo.size"] = new() { ["en"] = "Text size", ["th"] = "ขนาดตัวอักษร" },
        ["typo.sizeDesc"] = new()
        {
            ["en"] = "Zooms the whole window — text, icons and น้อง CluadeX together, so everything still fits. Applies at once and is remembered.",
            ["th"] = "ซูมทั้งหน้าต่าง ทั้งตัวอักษร ไอคอน และน้อง CluadeX ไปพร้อมกัน ทุกอย่างจึงยังพอดีเหมือนเดิม เปลี่ยนทันทีและจำไว้ใช้ครั้งต่อไป",
        },
        ["typo.smaller"] = new() { ["en"] = "Smaller", ["th"] = "เล็กลง" },
        ["typo.larger"] = new() { ["en"] = "Larger", ["th"] = "ใหญ่ขึ้น" },
        ["typo.reset"] = new() { ["en"] = "Back to 100 %", ["th"] = "กลับเป็น 100 %" },
        ["typo.preset.small"] = new() { ["en"] = "Small", ["th"] = "เล็ก" },
        ["typo.preset.normal"] = new() { ["en"] = "Normal", ["th"] = "ปกติ" },
        ["typo.preset.large"] = new() { ["en"] = "Large", ["th"] = "ใหญ่" },
        ["typo.preset.xlarge"] = new() { ["en"] = "Extra large", ["th"] = "ใหญ่มาก" },

        ["typo.font"] = new() { ["en"] = "Font", ["th"] = "ฟอนต์" },
        ["typo.fontDesc"] = new()
        {
            ["en"] = "The font of menus, buttons and chat. Code keeps its monospaced font.",
            ["th"] = "ฟอนต์ของเมนู ปุ่ม และแชท ส่วนโค้ดยังใช้ฟอนต์ความกว้างเท่ากันเหมือนเดิม",
        },
        ["typo.font.theme"] = new() { ["en"] = "The theme's own font (default)", ["th"] = "ตามธีม (ค่าเริ่มต้น)" },
        ["typo.font.bundled"] = new() { ["en"] = "Noto Sans — comes with CluadeX", ["th"] = "Noto Sans — มาพร้อม CluadeX" },
        ["typo.font.bundledTip"] = new()
        {
            ["en"] = "Free and open (SIL Open Font License 1.1): English and other Latin text in four real weights, plus Greek, Cyrillic and Vietnamese. Thai is drawn by Leelawadee UI, which comes with Windows and is the one Thai font whose vowels and tone marks this app's text engine places correctly.",
            ["th"] = "ฟรีและเปิด (สัญญาอนุญาต SIL Open Font License 1.1) ตัวอังกฤษ/ละตินหนาจริง 4 ระดับ รวมถึงกรีก ซีริลลิก และเวียดนาม ส่วนตัวไทยใช้ Leelawadee UI ที่มากับ Windows ทุกเครื่อง เพราะเป็นฟอนต์ไทยที่ระบบแสดงผลของโปรแกรมนี้วางสระและวรรณยุกต์ได้ถูกตำแหน่ง",
        },
        ["typo.font.installed"] = new() { ["en"] = "Installed on this PC", ["th"] = "ฟอนต์ที่ติดตั้งในเครื่องนี้" },
        ["typo.font.claudeNote"] = new()
        {
            ["en"] = "Why not Claude's own font? The fonts in Anthropic's Claude app are commercial and licensed to Anthropic — CluadeX may not ship them. Noto Sans is the free alternative. Some Thai fonts you install yourself may show tone marks out of place here; if so, go back to the default.",
            ["th"] = "ทำไมไม่ใช้ฟอนต์เดียวกับ Claude? ฟอนต์ในแอป Claude ของ Anthropic เป็นฟอนต์เชิงพาณิชย์ที่ Anthropic ซื้อสิทธิ์ใช้ CluadeX นำมาแจกต่อไม่ได้ จึงใส่ Noto Sans ซึ่งใช้ได้ฟรีถูกลิขสิทธิ์แทน ฟอนต์ไทยบางตัวที่ติดตั้งเองอาจวางวรรณยุกต์เพี้ยนในโปรแกรมนี้ ถ้าเห็นเพี้ยนให้กลับไปใช้ค่าเริ่มต้น",
        },
        ["typo.preview"] = new() { ["en"] = "Preview", ["th"] = "ตัวอย่าง" },
        ["typo.previewText"] = new()
        {
            ["en"] = "The quick brown fox jumps over the lazy dog · 0123456789",
            ["th"] = "น้ำใจไมตรี ปั๊มน้ำ ญี่ปุ่น กี่ปี ฎีกา ฤๅษี — สระและวรรณยุกต์ซ้อนกันได้ถูกที่",
        },
        ["typo.licence"] = new() { ["en"] = "Font licence (OFL 1.1)", ["th"] = "สัญญาอนุญาตฟอนต์ (OFL 1.1)" },
        ["typo.licenceTip"] = new()
        {
            ["en"] = "Opens the SIL Open Font License that ships with the bundled font.",
            ["th"] = "เปิดสัญญาอนุญาต SIL Open Font License ที่แนบมากับฟอนต์",
        },
    };
}
