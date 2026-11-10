namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the GPU share slider on the status-bar VRAM graph. Every key needs both "en" and "th".</summary>
internal static class GpuShareKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["gpushare.title"] = new() { ["en"] = "GPU FOR LOCAL MODELS", ["th"] = "GPU สำหรับโมเดลในเครื่อง" },
        ["gpushare.graphTip"] = new()
        {
            ["en"] = "Click to choose how much of the GPU local models may use",
            ["th"] = "คลิกเพื่อกำหนดว่าโมเดลในเครื่องใช้ GPU ได้กี่เปอร์เซ็นต์",
        },
        ["gpushare.full"] = new() { ["en"] = "No cap — the model may fill the whole GPU", ["th"] = "ไม่จำกัด — โมเดลใช้ GPU ได้เต็มที่" },
        ["gpushare.cpu"] = new()
        {
            ["en"] = "CPU only — the GPU stays free, answers are much slower",
            ["th"] = "ใช้ CPU อย่างเดียว — GPU ว่างให้งานอื่น แต่ตอบช้าลงมาก",
        },
        ["gpushare.partial"] = new()
        {
            ["en"] = "The rest runs on the CPU from system RAM — slower, but VRAM is left for other apps",
            ["th"] = "ส่วนที่เหลือรันบน CPU จาก RAM — ช้าลง แต่เหลือ VRAM ให้โปรแกรมอื่น",
        },
        ["gpushare.budget"] = new() { ["en"] = "≈ {0} of {1} VRAM", ["th"] = "≈ {0} จาก VRAM {1}" },
        ["gpushare.budgetNoGpu"] = new() { ["en"] = "No NVIDIA GPU reading", ["th"] = "อ่านค่า GPU NVIDIA ไม่ได้" },
        ["gpushare.layers"] = new()
        {
            ["en"] = "{0}: {1} / {2} layers on the GPU (≈ {3} of weights)",
            ["th"] = "{0}: {1} / {2} เลเยอร์อยู่บน GPU (น้ำหนักโมเดลราว {3})",
        },
        ["gpushare.layersAll"] = new()
        {
            ["en"] = "{0}: all {1} layers on the GPU (≈ {2})",
            ["th"] = "{0}: ทั้ง {1} เลเยอร์อยู่บน GPU (ราว {2})",
        },
        ["gpushare.layersCpu"] = new() { ["en"] = "{0}: every layer on the CPU", ["th"] = "{0}: ทุกเลเยอร์อยู่บน CPU" },
        ["gpushare.layersUnknown"] = new()
        {
            ["en"] = "{0}: its layer count can't be read, so the cap can't be applied to it",
            ["th"] = "{0}: อ่านจำนวนเลเยอร์ไม่ได้ จึงจำกัดการใช้ GPU กับโมเดลนี้ไม่ได้",
        },
        ["gpushare.noModel"] = new()
        {
            ["en"] = "No local model picked yet — the share applies to whichever you load",
            ["th"] = "ยังไม่ได้เลือกโมเดลในเครื่อง — ค่านี้จะใช้กับโมเดลที่โหลดครั้งถัดไป",
        },
        ["gpushare.ollama"] = new()
        {
            ["en"] = "Ollama: {0}% of the model's layers on the GPU (num_gpu)",
            ["th"] = "Ollama: {0}% ของเลเยอร์โมเดลอยู่บน GPU (num_gpu)",
        },
        ["gpushare.ollamaFull"] = new() { ["en"] = "Ollama decides by itself", ["th"] = "ให้ Ollama จัดการเอง" },
        ["gpushare.nextLoad"] = new()
        {
            ["en"] = "Takes effect the next time a model loads.",
            ["th"] = "มีผลเมื่อโหลดโมเดลครั้งถัดไป",
        },
        ["gpushare.nextOllama"] = new()
        {
            ["en"] = "Takes effect on your next message — Ollama reloads the model then.",
            ["th"] = "มีผลกับข้อความถัดไป — ตอนนั้น Ollama จะโหลดโมเดลใหม่เอง",
        },
        ["gpushare.loadedAt"] = new()
        {
            ["en"] = "The loaded model still uses {0}%.",
            ["th"] = "โมเดลที่โหลดอยู่ยังใช้ {0}% อยู่",
        },
        ["gpushare.reload"] = new() { ["en"] = "Reload model now", ["th"] = "โหลดโมเดลใหม่ตอนนี้" },
        ["gpushare.reloadConfirm"] = new()
        {
            ["en"] = "Reload {0} with {1}% of the GPU?\n\nThe model is unloaded and loaded again — the chat waits until it's ready.",
            ["th"] = "โหลด {0} ใหม่ โดยใช้ GPU {1}% ไหม?\n\nโมเดลจะถูกปิดแล้วโหลดใหม่ — แชทจะรอจนพร้อม",
        },
        ["gpushare.reloadTitle"] = new() { ["en"] = "Reload the model", ["th"] = "โหลดโมเดลใหม่" },
        ["gpushare.reloadBusy"] = new()
        {
            ["en"] = "Wait for the current answer or load to finish first.",
            ["th"] = "รอให้คำตอบหรือการโหลดที่ทำอยู่เสร็จก่อนนะ",
        },
        ["gpushare.reloading"] = new() { ["en"] = "Reloading {0} at {1}% GPU…", ["th"] = "กำลังโหลด {0} ใหม่ที่ GPU {1}%…" },
        ["gpushare.reloaded"] = new() { ["en"] = "{0} reloaded at {1}% GPU", ["th"] = "โหลด {0} ใหม่แล้ว ใช้ GPU {1}%" },
        ["gpushare.reloadFailed"] = new()
        {
            ["en"] = "Reloading {0} failed — see the Models page",
            ["th"] = "โหลด {0} ใหม่ไม่สำเร็จ — ดูรายละเอียดที่หน้าโมเดล",
        },
        ["gpushare.hint"] = new()
        {
            ["en"] = "Psst — click this VRAM graph to choose how much of the GPU local models may use",
            ["th"] = "รู้ไหม — คลิกกราฟ VRAM ตรงนี้เพื่อกำหนดว่าโมเดลในเครื่องใช้ GPU ได้กี่เปอร์เซ็นต์",
        },
        ["gpushare.hintDismiss"] = new() { ["en"] = "Got it", ["th"] = "เข้าใจแล้ว" },
        ["gpushare.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
    };
}
