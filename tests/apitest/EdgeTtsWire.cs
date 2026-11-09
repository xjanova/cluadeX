using System.IO;
using CluadeX.Services.Voice.Tts;

namespace ApiTest;

/// <summary>
/// Edge's free online voices (EdgeTts, 2026-11-09). Offline: the Sec-MS-GEC v2 token against vectors made with
/// edge-tts 7.3.1's own drm.py (generate_sec_ms_gec_v2), the SSML, the voice for a reply's language. Live (--edge):
/// a Thai and an English line spoken by the real service.
/// </summary>
internal static class EdgeTtsWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("Edge voices — offline");
        (double At, string Token)[] vectors =
        [
            (0, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6"),
            (1700000000, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2"),
            (1762700000.5, "5D1B18B0CB4E06AC-09B891CF8CBB90F79E0237E64F1FDA9BE0F4A0A8613A599B0F50D7CE5A9B1259"),
            (1893456000, "7442B6E630CDF0DE-CE82EC1509335DC856270C1EEB0F7F2575909AC26840D2C15B28A41EBB7E9CCF"),
        ];
        foreach (var (at, want) in vectors)
        {
            string got = EdgeTts.Token(at);
            check("edge", $"Sec-MS-GEC v2 at {at} matches edge-tts", got == want, got);
        }
        // 1700000000 + the Windows epoch is 200 s into its 5-minute step: the step ends at 1700000099.
        check("edge", "the token holds for its 5-minute step and changes after",
            EdgeTts.Token(1699999800) == EdgeTts.Token(1700000099) && EdgeTts.Token(1700000099) != EdgeTts.Token(1700000100), "");

        string ssml = EdgeTts.Ssml("ราคา <10> & \"ดี\"\v", "th-TH-PremwadeeNeural", "+10%", "+20Hz");
        check("edge", "SSML escapes the text and keeps the voice", ssml.Contains("ราคา &lt;10&gt; &amp; &quot;ดี&quot; ")
            && ssml.Contains("<voice name='th-TH-PremwadeeNeural'>") && ssml.Contains("rate='+10%'") && ssml.Contains("pitch='+20Hz'"), ssml);
        check("edge", "a Thai reply gets a Thai voice even when an English one is chosen",
            EdgeTts.VoiceFor("en-US-AvaMultilingualNeural", thai: true) == "th-TH-PremwadeeNeural", "");
        check("edge", "the chosen voice is kept for its own language",
            EdgeTts.VoiceFor("th-TH-NiwatNeural", thai: true) == "th-TH-NiwatNeural"
            && EdgeTts.VoiceFor("en-US-AnaNeural", thai: false) == "en-US-AnaNeural", "");
        check("edge", "rate and pitch in the service's units", EdgeTts.RateText(1.25) == "+25%" && EdgeTts.RateText(0.9) == "-10%"
            && EdgeTts.PitchText(1.1) == "+20Hz" && EdgeTts.PitchText(1) == "+0Hz", EdgeTts.RateText(0.9) + " " + EdgeTts.PitchText(1.1));
        return Task.CompletedTask;
    }

    public static async Task RunLive(Action<string, string, bool, string> check, Action<string> section)
    {
        section("Edge voices — live");
        foreach (var (text, voice) in new[]
                 {
                     ("สวัสดีค่ะ หนูคือคลาวด์เอ็กซ์ ทดสอบเสียงภาษาไทยค่ะ", "th-TH-PremwadeeNeural"),
                     ("Hello, this is CluadeX reading a reply aloud.", "en-US-AvaMultilingualNeural"),
                 })
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var mp3 = await EdgeTts.SpeakAsync(text, voice, "+0%", "+0Hz");
                bool frames = mp3.Length > 4000 && (mp3[0] == 0xFF || (mp3[0] == 'I' && mp3[1] == 'D' && mp3[2] == '3'));
                check("edge", $"{voice} speaks", frames, $"{mp3.Length} bytes in {sw.ElapsedMilliseconds} ms");
                if (Environment.GetEnvironmentVariable("CX_EDGE_OUT") is { Length: > 0 } dir)
                    File.WriteAllBytes(Path.Combine(dir, voice + ".mp3"), mp3);
            }
            catch (Exception ex) { check("edge", $"{voice} speaks", false, ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
