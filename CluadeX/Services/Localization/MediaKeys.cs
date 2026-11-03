namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the media editor (images, audio, video in an editor tab — Views/Editor/MediaEditorHost,
/// Resources/MediaEditor/media.html). "media.page.*" are the page's own words: the host sends all of them to the page
/// in the UI language (the page has no text of its own). Every key needs both "en" and "th".</summary>
internal static class MediaKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── status line / questions (view model, host) ──
        ["media.status.opened"] = new() { ["en"] = "Opened {0} · {1}", ["th"] = "เปิด {0} แล้ว · {1}" },
        ["media.status.gone"] = new() { ["en"] = "{0} is not on disk any more.", ["th"] = "ไม่พบไฟล์ {0} บนดิสก์แล้ว" },
        ["media.status.nothingToSave"] = new() { ["en"] = "{0} has no edits to save.", ["th"] = "{0} ยังไม่มีการแก้ไขที่ต้องบันทึก" },
        ["media.status.noEditor"] = new()
        {
            ["en"] = "{0}: the media editor is not open, so nothing was saved. Show the tab and save from there.",
            ["th"] = "{0}: ตัวแก้ไขสื่อไม่ได้เปิดอยู่ จึงยังไม่ได้บันทึก — เปิดแท็บนั้นแล้วบันทึกจากที่นั่น",
        },
        ["media.status.busy"] = new() { ["en"] = "{0} is still saving — wait for it to finish.", ["th"] = "{0} ยังบันทึกอยู่ — รอให้เสร็จก่อน" },
        ["media.status.savedCopy"] = new() { ["en"] = "Saved {0}", ["th"] = "บันทึก {0} แล้ว" },
        ["media.status.overwritten"] = new() { ["en"] = "Saved over {0}", ["th"] = "บันทึกทับ {0} แล้ว" },
        ["media.status.saveFailed"] = new()
        {
            ["en"] = "Could not save {0}. Nothing was written — the Debug Log has the details.",
            ["th"] = "บันทึก {0} ไม่สำเร็จ ไม่มีไฟล์ใดถูกเขียน — ดูรายละเอียดได้ใน Debug Log",
        },
        ["media.status.saveCancelled"] = new() { ["en"] = "Saving {0} was stopped — nothing was written.", ["th"] = "หยุดบันทึก {0} แล้ว — ไม่มีไฟล์ใดถูกเขียน" },
        ["media.status.ffmpegFailed"] = new()
        {
            ["en"] = "ffmpeg could not cut {0}. Nothing was written — the Debug Log has ffmpeg's message.",
            ["th"] = "ffmpeg ตัด {0} ไม่สำเร็จ ไม่มีไฟล์ใดถูกเขียน — ดูข้อความของ ffmpeg ได้ใน Debug Log",
        },
        ["media.status.ffmpegTimeout"] = new()
        {
            ["en"] = "Cutting {0} took too long and was stopped. Nothing was written.",
            ["th"] = "การตัด {0} ใช้เวลานานเกินไปจึงถูกหยุด ไม่มีไฟล์ใดถูกเขียน",
        },
        ["media.status.ffmpegMissing"] = new()
        {
            ["en"] = "Cutting {0} in its own format needs ffmpeg, which was not found on PATH.",
            ["th"] = "การตัด {0} โดยคงรูปแบบไฟล์เดิมต้องใช้ ffmpeg แต่ไม่พบ ffmpeg ใน PATH",
        },
        ["media.status.sameFormatOnly"] = new()
        {
            ["en"] = "Only a file of the same format can replace {0} — save a copy instead.",
            ["th"] = "บันทึกทับ {0} ได้เฉพาะไฟล์รูปแบบเดียวกัน — ให้บันทึกเป็นสำเนาแทน",
        },
        ["media.status.tooLarge"] = new()
        {
            ["en"] = "The result for {0} is larger than 2 GB — it was not saved.",
            ["th"] = "ผลลัพธ์ของ {0} ใหญ่เกิน 2 GB จึงไม่ได้บันทึก",
        },

        ["media.overwrite.title"] = new() { ["en"] = "Replace the original file?", ["th"] = "บันทึกทับไฟล์ต้นฉบับ?" },
        ["media.overwrite.body"] = new()
        {
            ["en"] = "{0} will be replaced by the edited version. The original cannot be brought back from CluadeX (git or a backup still can).\n\nTo keep the original, choose Cancel and use Save copy.",
            ["th"] = "{0} จะถูกแทนที่ด้วยไฟล์ที่แก้ไขแล้ว และกู้ต้นฉบับคืนจาก CluadeX ไม่ได้ (กู้จาก git หรือไฟล์สำรองได้)\n\nถ้าต้องการเก็บต้นฉบับไว้ ให้กด ยกเลิก แล้วใช้ บันทึกเป็นสำเนา",
        },
        ["media.overwrite.yes"] = new() { ["en"] = "Replace the original", ["th"] = "บันทึกทับต้นฉบับ" },

        ["media.busyClose.title"] = new() { ["en"] = "Still saving", ["th"] = "ยังบันทึกอยู่" },
        ["media.busyClose.body"] = new()
        {
            ["en"] = "{0} is still being saved. Closing the tab stops it, and the unfinished file is removed.",
            ["th"] = "{0} ยังบันทึกไม่เสร็จ ถ้าปิดแท็บตอนนี้ การบันทึกจะหยุด และไฟล์ที่ยังเขียนไม่เสร็จจะถูกลบ",
        },
        ["media.busyClose.stop"] = new() { ["en"] = "Stop and close", ["th"] = "หยุดและปิด" },

        ["media.noWebView2.title"] = new() { ["en"] = "The media editor cannot start", ["th"] = "เปิดตัวแก้ไขสื่อไม่ได้" },
        ["media.noWebView2.body"] = new()
        {
            ["en"] = "Showing and editing images, audio and video needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC. Install it from Microsoft, then open the file again.",
            ["th"] = "การแสดงและแก้ไขรูปภาพ เสียง และวิดีโอต้องใช้ Microsoft Edge WebView2 Runtime ซึ่งยังไม่ได้ติดตั้งในเครื่องนี้ ติดตั้งจาก Microsoft แล้วเปิดไฟล์อีกครั้ง",
        },
        ["media.engineFailed.body"] = new()
        {
            ["en"] = "The media editor could not start. Close the tab and open the file again; the Debug Log has the details.",
            ["th"] = "ตัวแก้ไขสื่อเริ่มทำงานไม่ได้ ปิดแท็บแล้วเปิดไฟล์อีกครั้ง — ดูรายละเอียดได้ใน Debug Log",
        },
        ["media.gone.title"] = new() { ["en"] = "The file is gone", ["th"] = "ไม่พบไฟล์แล้ว" },
        ["media.gone.body"] = new()
        {
            ["en"] = "{0} was moved or deleted while it was open. Unsaved edits are still here — save a copy to keep them.",
            ["th"] = "{0} ถูกย้ายหรือลบไประหว่างที่เปิดอยู่ การแก้ไขที่ยังไม่บันทึกยังอยู่ — บันทึกเป็นสำเนาเพื่อเก็บไว้",
        },

        // ── the page ──
        ["media.page.undo"] = new() { ["en"] = "Undo (Ctrl+Z)", ["th"] = "เลิกทำ (Ctrl+Z)" },
        ["media.page.redo"] = new() { ["en"] = "Redo (Ctrl+Y)", ["th"] = "ทำซ้ำ (Ctrl+Y)" },
        ["media.page.crop"] = new() { ["en"] = "Crop", ["th"] = "ครอบตัด" },
        ["media.page.resize"] = new() { ["en"] = "Resize", ["th"] = "ปรับขนาด" },
        ["media.page.adjust"] = new() { ["en"] = "Light", ["th"] = "ความสว่าง" },
        ["media.page.rotateLeft"] = new() { ["en"] = "Rotate left 90°", ["th"] = "หมุนซ้าย 90°" },
        ["media.page.rotateRight"] = new() { ["en"] = "Rotate right 90°", ["th"] = "หมุนขวา 90°" },
        ["media.page.flipH"] = new() { ["en"] = "Flip horizontally", ["th"] = "พลิกซ้าย-ขวา" },
        ["media.page.flipV"] = new() { ["en"] = "Flip vertically", ["th"] = "พลิกบน-ล่าง" },
        ["media.page.fit"] = new() { ["en"] = "Fit (0)", ["th"] = "พอดีหน้าจอ (0)" },
        ["media.page.zoomIn"] = new() { ["en"] = "Zoom in (+)", ["th"] = "ซูมเข้า (+)" },
        ["media.page.zoomOut"] = new() { ["en"] = "Zoom out (−)", ["th"] = "ซูมออก (−)" },
        ["media.page.zoomSel"] = new() { ["en"] = "Zoom to the selection", ["th"] = "ซูมไปที่ช่วงที่เลือก" },
        ["media.page.actualSize"] = new() { ["en"] = "Actual size (100%)", ["th"] = "ขนาดจริง (100%)" },
        ["media.page.editAsText"] = new() { ["en"] = "Edit as text", ["th"] = "แก้ไขเป็นข้อความ" },
        ["media.page.viewOnly"] = new() { ["en"] = "view only", ["th"] = "ดูได้อย่างเดียว" },
        ["media.page.width"] = new() { ["en"] = "Width", ["th"] = "กว้าง" },
        ["media.page.height"] = new() { ["en"] = "Height", ["th"] = "สูง" },
        ["media.page.keepRatio"] = new() { ["en"] = "Keep ratio", ["th"] = "คงสัดส่วน" },
        ["media.page.apply"] = new() { ["en"] = "Apply", ["th"] = "ใช้" },
        ["media.page.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["media.page.brightness"] = new() { ["en"] = "Brightness", ["th"] = "ความสว่าง" },
        ["media.page.contrast"] = new() { ["en"] = "Contrast", ["th"] = "คอนทราสต์" },
        ["media.page.ratio"] = new() { ["en"] = "Ratio", ["th"] = "สัดส่วน" },
        ["media.page.ratioFree"] = new() { ["en"] = "Free", ["th"] = "อิสระ" },
        ["media.page.ratioOriginal"] = new() { ["en"] = "Original", ["th"] = "ตามต้นฉบับ" },
        ["media.page.cropHint"] = new() { ["en"] = "Drag the box or its corners · Enter applies · Esc cancels", ["th"] = "ลากกรอบหรือมุมกรอบ · Enter = ใช้ · Esc = ยกเลิก" },
        ["media.page.badSize"] = new() { ["en"] = "That size is not possible (1 to 32767 pixels a side).", ["th"] = "ขนาดนี้ใช้ไม่ได้ (ด้านละ 1 ถึง 32767 พิกเซล)" },
        ["media.page.tooLargeToEdit"] = new() { ["en"] = "This picture is too large to edit here — it is shown only.", ["th"] = "รูปนี้ใหญ่เกินกว่าจะแก้ไขที่นี่ได้ — แสดงให้ดูอย่างเดียว" },
        ["media.page.gifFirstFrame"] = new() { ["en"] = "An edited GIF is saved as a still picture (its first frame).", ["th"] = "GIF ที่แก้ไขจะถูกบันทึกเป็นภาพนิ่ง (เฟรมแรก)" },
        ["media.page.editFailed"] = new() { ["en"] = "That edit did not work on this file.", ["th"] = "แก้ไขแบบนี้กับไฟล์นี้ไม่สำเร็จ" },
        ["media.page.formatTip"] = new() { ["en"] = "Format of the saved picture", ["th"] = "รูปแบบไฟล์ภาพที่จะบันทึก" },
        ["media.page.quality"] = new() { ["en"] = "Quality", ["th"] = "คุณภาพ" },
        ["media.page.qualityTip"] = new() { ["en"] = "JPEG / WebP quality", ["th"] = "คุณภาพของ JPEG / WebP" },
        ["media.page.saveCopy"] = new() { ["en"] = "Save copy", ["th"] = "บันทึกเป็นสำเนา" },
        ["media.page.saveCopyTip"] = new() { ["en"] = "Save the edits as a new file next to the original (the original stays as it is)", ["th"] = "บันทึกการแก้ไขเป็นไฟล์ใหม่ข้างไฟล์ต้นฉบับ (ต้นฉบับไม่ถูกแตะ)" },
        ["media.page.saveCopyWav"] = new() { ["en"] = "Save copy (WAV)", ["th"] = "บันทึกเป็นสำเนา (WAV)" },
        ["media.page.saveCopyLossless"] = new() { ["en"] = "Save copy (same format, lossless)", ["th"] = "บันทึกเป็นสำเนา (รูปแบบเดิม ไม่เสียคุณภาพ)" },
        ["media.page.saveCutLossless"] = new() { ["en"] = "Save cut (same format, lossless)", ["th"] = "บันทึกช่วงที่ตัด (รูปแบบเดิม ไม่เสียคุณภาพ)" },
        ["media.page.saveCutRecord"] = new() { ["en"] = "Save cut (record as WebM)", ["th"] = "บันทึกช่วงที่ตัด (อัดเป็น WebM)" },
        ["media.page.overwrite"] = new() { ["en"] = "Overwrite…", ["th"] = "บันทึกทับ…" },
        ["media.page.overwriteTip"] = new() { ["en"] = "Replace the original file with the edits (asks first)", ["th"] = "แทนที่ไฟล์ต้นฉบับด้วยการแก้ไข (จะถามก่อน)" },
        ["media.page.overwriteSameFormat"] = new() { ["en"] = "Only the original's own format can replace it — pick it in the format list, or save a copy.", ["th"] = "บันทึกทับได้เฉพาะรูปแบบเดียวกับต้นฉบับ — เลือกรูปแบบนั้นในรายการ หรือบันทึกเป็นสำเนา" },
        ["media.page.overwriteOnlyWav"] = new() { ["en"] = "These edits are saved as WAV, so only a WAV original can be replaced — save a copy instead.", ["th"] = "การแก้ไขนี้บันทึกเป็น WAV จึงบันทึกทับได้เฉพาะต้นฉบับที่เป็น WAV — ให้บันทึกเป็นสำเนาแทน" },
        ["media.page.cantWriteFormat"] = new() { ["en"] = "These edits cannot be written as {0}.", ["th"] = "การแก้ไขนี้บันทึกเป็น {0} ไม่ได้" },
        ["media.page.saving"] = new() { ["en"] = "Saving…", ["th"] = "กำลังบันทึก…" },
        ["media.page.cutting"] = new() { ["en"] = "Cutting with ffmpeg…", ["th"] = "กำลังตัดด้วย ffmpeg…" },
        ["media.page.recording"] = new() { ["en"] = "Recording the cut — it plays through in real time ({0} s)…", ["th"] = "กำลังอัดช่วงที่ตัด — ต้องเล่นจริงตามความยาว ({0} วินาที)…" },
        ["media.page.saved"] = new() { ["en"] = "Saved {0}", ["th"] = "บันทึก {0} แล้ว" },
        ["media.page.saveFailed"] = new() { ["en"] = "Could not save — nothing was written.", ["th"] = "บันทึกไม่สำเร็จ — ไม่มีไฟล์ใดถูกเขียน" },
        ["media.page.saveCancelled"] = new() { ["en"] = "Not saved.", ["th"] = "ไม่ได้บันทึก" },
        ["media.page.needsFfmpeg"] = new() { ["en"] = "Replacing the original video needs ffmpeg (not found) — save a copy instead.", ["th"] = "การบันทึกทับวิดีโอต้นฉบับต้องใช้ ffmpeg (ไม่พบ) — ให้บันทึกเป็นสำเนาแทน" },

        ["media.page.play"] = new() { ["en"] = "Play / pause", ["th"] = "เล่น / หยุดชั่วคราว" },
        ["media.page.playTip"] = new() { ["en"] = "Play / pause (Space)", ["th"] = "เล่น / หยุดชั่วคราว (Space)" },
        ["media.page.stop"] = new() { ["en"] = "Stop", ["th"] = "หยุด" },
        ["media.page.loop"] = new() { ["en"] = "Loop the selection", ["th"] = "เล่นวนช่วงที่เลือก" },
        ["media.page.volume"] = new() { ["en"] = "Volume", ["th"] = "ระดับเสียง" },
        ["media.page.mute"] = new() { ["en"] = "Mute (M)", ["th"] = "ปิดเสียง (M)" },
        ["media.page.trim"] = new() { ["en"] = "Trim", ["th"] = "ตัดให้เหลือช่วงนี้" },
        ["media.page.trimTip"] = new() { ["en"] = "Keep only the selection", ["th"] = "เก็บไว้เฉพาะช่วงที่เลือก" },
        ["media.page.deleteSel"] = new() { ["en"] = "Delete the selection", ["th"] = "ลบช่วงที่เลือก" },
        ["media.page.deleteSelTip"] = new() { ["en"] = "Delete the selection (Delete)", ["th"] = "ลบช่วงที่เลือก (Delete)" },
        ["media.page.silence"] = new() { ["en"] = "Silence the selection", ["th"] = "ทำให้ช่วงที่เลือกเงียบ" },
        ["media.page.fadeIn"] = new() { ["en"] = "Fade in (the selection, or the first second)", ["th"] = "เฟดเข้า (ช่วงที่เลือก หรือวินาทีแรก)" },
        ["media.page.fadeOut"] = new() { ["en"] = "Fade out (the selection, or the last second)", ["th"] = "เฟดออก (ช่วงที่เลือก หรือวินาทีสุดท้าย)" },
        ["media.page.gain"] = new() { ["en"] = "Volume", ["th"] = "ระดับเสียง" },
        ["media.page.gainSel"] = new() { ["en"] = "Change the selection's volume", ["th"] = "ปรับระดับเสียงช่วงที่เลือก" },
        ["media.page.gainAll"] = new() { ["en"] = "Change the volume of the whole file", ["th"] = "ปรับระดับเสียงทั้งไฟล์" },
        ["media.page.normalize"] = new() { ["en"] = "Normalize (peak to −1 dB)", ["th"] = "ปรับระดับให้สูงสุดที่ −1 dB (Normalize)" },
        ["media.page.selectFirst"] = new() { ["en"] = "Drag across the waveform to select a part first.", ["th"] = "ลากบนรูปคลื่นเพื่อเลือกช่วงก่อน" },
        ["media.page.cantDeleteAll"] = new() { ["en"] = "Deleting everything would leave nothing to save.", ["th"] = "ลบทั้งหมดแล้วจะไม่เหลืออะไรให้บันทึก" },
        ["media.page.selection"] = new() { ["en"] = "selected {0} – {1} ({2} s)", ["th"] = "เลือก {0} – {1} ({2} วินาที)" },
        ["media.page.loadingAudio"] = new() { ["en"] = "Reading the sound…", ["th"] = "กำลังอ่านเสียง…" },
        ["media.page.noWaveform"] = new() { ["en"] = "This file plays, but its sound cannot be read for a waveform or edits.", ["th"] = "ไฟล์นี้เล่นได้ แต่อ่านข้อมูลเสียงมาแสดงรูปคลื่นหรือแก้ไขไม่ได้" },
        ["media.page.audioTooLong"] = new() { ["en"] = "Waveform and edits are for files up to {0} minutes — this one plays only.", ["th"] = "รูปคลื่นและการแก้ไขใช้ได้กับไฟล์ยาวไม่เกิน {0} นาที — ไฟล์นี้เล่นได้อย่างเดียว" },
        ["media.page.playOnly"] = new() { ["en"] = "Play only", ["th"] = "เล่นได้อย่างเดียว" },

        ["media.page.setIn"] = new() { ["en"] = "In", ["th"] = "จุดเริ่ม" },
        ["media.page.setInTip"] = new() { ["en"] = "Start the cut here (I)", ["th"] = "ตั้งจุดเริ่มของช่วงที่ตัดที่ตำแหน่งนี้ (I)" },
        ["media.page.setOut"] = new() { ["en"] = "Out", ["th"] = "จุดจบ" },
        ["media.page.setOutTip"] = new() { ["en"] = "End the cut here (O)", ["th"] = "ตั้งจุดจบของช่วงที่ตัดที่ตำแหน่งนี้ (O)" },
        ["media.page.playCut"] = new() { ["en"] = "Play the cut", ["th"] = "เล่นช่วงที่ตัด" },
        ["media.page.clearCut"] = new() { ["en"] = "Clear the cut (whole video)", ["th"] = "ล้างช่วงที่ตัด (ทั้งวิดีโอ)" },
        ["media.page.cutRange"] = new() { ["en"] = "cut {0} – {1} ({2} s)", ["th"] = "ช่วงที่ตัด {0} – {1} ({2} วินาที)" },
        ["media.page.snapshot"] = new() { ["en"] = "Snapshot", ["th"] = "จับภาพ" },
        ["media.page.snapshotTip"] = new() { ["en"] = "Save this frame as a PNG next to the video", ["th"] = "บันทึกเฟรมนี้เป็น PNG ข้างไฟล์วิดีโอ" },
        ["media.page.frameBack"] = new() { ["en"] = "Previous frame (,)", ["th"] = "เฟรมก่อนหน้า (,)" },
        ["media.page.frameFwd"] = new() { ["en"] = "Next frame (.)", ["th"] = "เฟรมถัดไป (.)" },
        ["media.page.noVideoTrack"] = new() { ["en"] = "The picture of this video cannot be shown (its codec) — only the sound plays.", ["th"] = "แสดงภาพของวิดีโอนี้ไม่ได้ (codec ไม่รองรับ) — เล่นได้เฉพาะเสียง" },

        ["media.page.emptyTitle"] = new() { ["en"] = "Empty file", ["th"] = "ไฟล์ว่าง" },
        ["media.page.emptyBody"] = new() { ["en"] = "This file has 0 bytes — there is nothing to show.", ["th"] = "ไฟล์นี้มีขนาด 0 ไบต์ — ไม่มีอะไรให้แสดง" },
        ["media.page.unsupportedTitle"] = new() { ["en"] = "This format cannot be played here", ["th"] = "เล่นรูปแบบไฟล์นี้ที่นี่ไม่ได้" },
        ["media.page.unsupportedBody"] = new()
        {
            ["en"] = "The built-in player does not support this file's codec. Open it in another app, or convert it (for example with ffmpeg) to MP4 (H.264), WebM, MP3 or WAV.",
            ["th"] = "เครื่องเล่นในตัวไม่รองรับ codec ของไฟล์นี้ เปิดด้วยโปรแกรมอื่น หรือแปลงไฟล์ (เช่นด้วย ffmpeg) เป็น MP4 (H.264), WebM, MP3 หรือ WAV",
        },
        ["media.page.cantShowTitle"] = new() { ["en"] = "This file cannot be shown", ["th"] = "แสดงไฟล์นี้ไม่ได้" },
        ["media.page.cantShowBody"] = new()
        {
            ["en"] = "It may be damaged, or not really the format its name says.",
            ["th"] = "ไฟล์อาจเสียหาย หรือไม่ได้เป็นรูปแบบตามนามสกุลจริง ๆ",
        },
    };
}
