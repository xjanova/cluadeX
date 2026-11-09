using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Security;
using System.Text;

namespace CluadeX.Services.Voice.Tts;

/// <summary>
/// Microsoft Edge's online read-aloud voices — the "Read aloud" voices of the Edge browser (2026-11-09). Free, no
/// account, no key, natural Thai (th-TH-PremwadeeNeural, th-TH-NiwatNeural) — the free voice for a PC whose Windows
/// has no Thai voice (most: System.Speech sees only David and Zira, and an English voice reads Thai as silence).
/// <para>
/// The service is the one Edge itself calls; what it expects follows the edge-tts project (rany2/edge-tts 7.3.1):
/// a WebSocket, a speech.config message, an SSML message, MP3 frames back until turn.end. Each request carries a
/// Sec-MS-GEC token: SHA-256 of a hidden 57-byte salt and "&lt;Windows file time, 5-minute steps&gt;&lt;client token&gt;",
/// plus a CS64 digest (version 2, from late 2026; version 1 now gets 403). Microsoft changes this now and then —
/// when it breaks, compare with edge-tts' drm.py; the test vectors in tests/apitest/EdgeTtsWire.cs come from it.
/// </para>
/// </summary>
public static class EdgeTts
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumFullVersion = "143.0.3650.75";
    public const string SecMsGecVersion = "2-" + ChromiumFullVersion;
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=" + TrustedClientToken;

    /// <summary>The address the tests point at a local mock.</summary>
    internal static string Endpoint { get; set; } = WssUrl;

    public sealed record Voice(string Name, string Label, string Lang, bool Female);

    /// <summary>The voices offered in Settings › Voice: Thai first, then English. The service has hundreds more; any
    /// name it knows works.</summary>
    public static readonly Voice[] Voices =
    [
        new("th-TH-PremwadeeNeural", "Premwadee — ไทย หญิง", "th", true),
        new("th-TH-NiwatNeural", "Niwat — ไทย ชาย", "th", false),
        new("en-US-AvaMultilingualNeural", "Ava — English, warm", "en", true),
        new("en-US-EmmaMultilingualNeural", "Emma — English, bright", "en", true),
        new("en-US-JennyNeural", "Jenny — English, friendly", "en", true),
        new("en-US-AriaNeural", "Aria — English, confident", "en", true),
        new("en-US-AnaNeural", "Ana — English, young", "en", true),
        new("en-GB-SoniaNeural", "Sonia — English (UK)", "en", true),
        new("en-US-AndrewMultilingualNeural", "Andrew — English, male", "en", false),
        new("en-US-GuyNeural", "Guy — English, male", "en", false),
    ];

    /// <summary>The voice for a reply: the chosen one when it speaks the reply's language (a multilingual English voice
    /// counts for English only), else the first voice of that language.</summary>
    public static string VoiceFor(string? chosen, bool thai)
    {
        string lang = thai ? "th" : "en";
        if (!string.IsNullOrWhiteSpace(chosen) && chosen.Trim().StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase)) return chosen.Trim();
        return Voices.First(v => v.Lang == lang).Name;
    }

    /// <summary>0.5-2 → "+25%"; the girl's own pace on top.</summary>
    public static string RateText(double rate) => Signed((int)Math.Round((Math.Clamp(rate, 0.5, 2) - 1) * 100)) + "%";

    /// <summary>0.5-2 → Hz around the voice's own pitch ("+20Hz"): a girl's voice sits near 200 Hz.</summary>
    public static string PitchText(double pitch) => Signed((int)Math.Round((Math.Clamp(pitch, 0.5, 2) - 1) * 200)) + "Hz";

    private static string Signed(int x) => (x >= 0 ? "+" : "") + x;

    /// <summary>The SSML one request carries.</summary>
    public static string Ssml(string text, string voice, string rate, string pitch) =>
        "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
        + $"<voice name='{SecurityElement.Escape(voice)}'><prosody pitch='{pitch}' rate='{rate}' volume='+0%'>"
        + SecurityElement.Escape(Clean(text)) + "</prosody></voice></speak>";

    /// <summary>The service refuses a few control characters (vertical tab, form feed …).</summary>
    private static string Clean(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s) b.Append(c is (>= '\0' and <= '\b') or '\v' or '\f' or (>= '\u000e' and <= '\u001f') ? ' ' : c);
        return b.ToString();
    }

    /// <summary>The service's own clock, learnt from a refused request's Date header (a PC clock that is off by minutes
    /// makes a token the service refuses).</summary>
    private static double _skewSeconds;

    /// <summary>Speak <paramref name="text"/> as 24 kHz mono MP3. Throws <see cref="EdgeTtsException"/> with the reason.</summary>
    public static async Task<byte[]> SpeakAsync(string text, string voice, string rate, string pitch, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(text) > 8000) text = text[..Math.Min(text.Length, 2600)];
        for (int attempt = 0; ; attempt++)
        {
            try { return await SpeakOnceAsync(text, voice, rate, pitch, ct).ConfigureAwait(false); }
            catch (WebSocketException ex) when (!ct.IsCancellationRequested)
            {
                // The service now and then drops a connection before the first frame (2026-11-09: 1 request in 3),
                // and a 403 means the token was refused — usually the PC's clock. Learn its time once, try again.
                if (attempt >= 2) throw new EdgeTtsException("the Edge voice service refused the request (" + ex.Message + ")", ex);
                if (attempt == 0) await LearnClockAsync(ct).ConfigureAwait(false);
                await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> SpeakOnceAsync(string text, string voice, string rate, string pitch, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        string major = ChromiumFullVersion.Split('.')[0];
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");
        string url = $"{Endpoint}&ConnectionId={Guid.NewGuid():N}&Sec-MS-GEC={Token(Now())}&Sec-MS-GEC-Version={SecMsGecVersion}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var t = timeout.Token;
        await ws.ConnectAsync(new Uri(url), t).ConfigureAwait(false);

        string stamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture);
        await SendText(ws, $"X-Timestamp:{stamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"
            + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
            + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n", t).ConfigureAwait(false);
        // "…Z" after a JavaScript date is not a mistake: Edge sends it so.
        await SendText(ws, $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{stamp}Z\r\nPath:ssml\r\n\r\n"
            + Ssml(text, voice, rate, pitch), t).ConfigureAwait(false);

        using var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, t).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                    throw new EdgeTtsException(audio.Length > 0 ? "the Edge voice service closed early" : "the Edge voice service sent no audio (is the voice name right?)");
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            var data = message.ToArray();
            if (r.MessageType == WebSocketMessageType.Text)
            {
                string head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 600));
                if (head.Contains("Path:turn.end", StringComparison.Ordinal)) break;
                continue;
            }
            // Binary: 2-byte header length (big endian), the header, the audio.
            if (data.Length < 2) continue;
            int headLen = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (headLen + 2 > data.Length) continue;
            string headers = Encoding.UTF8.GetString(data, 2, headLen);
            if (!headers.Contains("Path:audio", StringComparison.Ordinal)) continue;
            audio.Write(data, headLen + 2, data.Length - headLen - 2);
        }
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
        if (audio.Length == 0) throw new EdgeTtsException("the Edge voice service sent no audio (is the voice name right?)");
        return audio.ToArray();
    }

    private static Task SendText(ClientWebSocket ws, string s, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, ct);

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + _skewSeconds;

    /// <summary>Read the service's time from its Date header (a plain HTTPS request to the same host).</summary>
    private static async Task<bool> LearnClockAsync(CancellationToken ct)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync("https://speech.platform.bing.com/", ct).ConfigureAwait(false);
            if (resp.Headers.Date is not { } date) return false;
            double skew = date.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(skew - _skewSeconds) < 60) return false;   // the clock was not the reason
            _skewSeconds = skew;
            return true;
        }
        catch { return false; }
    }

    // ─── Sec-MS-GEC, version 2 (a port of edge-tts drm.py generate_sec_ms_gec_v2) ───

    private const long WinEpoch = 11644473600;

    /// <summary>"&lt;16 hex CS64&gt;-&lt;64 hex SHA-256&gt;" for <paramref name="unixSeconds"/>.</summary>
    public static string Token(double unixSeconds)
    {
        long s = (long)Math.Floor(unixSeconds) + WinEpoch;
        s -= s % 300;
        byte[] data = Encoding.ASCII.GetBytes((s * 10_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken);
        byte[] digest = Sha256Salted(data);
        var (c0, c1) = Cs64(data, digest);
        var cs = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cs, c0);
        BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(4), c1);
        return Convert.ToHexString(cs) + "-" + Convert.ToHexString(digest);
    }

    private static readonly uint[] ShaInitial = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];
    private static readonly uint[] ShaState = [0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2];
    private static readonly uint[] ShaSchedule = [0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564];
    private static readonly uint[] K =
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5, 0xD807AA98, 0x12835B01, 0x243185BE,
        0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174, 0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA,
        0x5CB0A9DC, 0x76F988DA, 0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967, 0x27B70A85,
        0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85, 0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3,
        0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070, 0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F,
        0x682E6FF3, 0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];
    private const uint ShaAdjustment = 0xC9DC06A7, Sigma0Adjustment = 0x059C0B80, Sigma1Adjustment = 0x000B92C0;
    private static readonly uint[] KFirstBlock = K.Select((k, i) => i == 14 ? ShaAdjustment : k).ToArray();

    private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
    private static uint SmallSigma0(uint v) => Rotr(v, 7) ^ Rotr(v, 18) ^ (v >> 3);
    private static uint SmallSigma1(uint v) => Rotr(v, 17) ^ Rotr(v, 19) ^ (v >> 10);

    private static void Rounds(uint[] state, uint[] w, int first, uint[] k)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = first; i < 64; i++)
        {
            uint t1 = unchecked(h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + (g ^ (e & (f ^ g))) + k[i] + w[i]);
            uint t2 = unchecked((Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) | (c & (a | b))));
            h = g; g = f; f = e; e = unchecked(d + t1); d = c; c = b; b = a; a = unchecked(t1 + t2);
        }
        state[0] = a; state[1] = b; state[2] = c; state[3] = d; state[4] = e; state[5] = f; state[6] = g; state[7] = h;
    }

    private static void Expand(uint[] w, int from)
    {
        for (int i = from; i < 64; i++)
            w[i] = unchecked(SmallSigma1(w[i - 2]) + w[i - 7] + SmallSigma0(w[i - 15]) + w[i - 16]);
    }

    /// <summary>The state after the first block: the hidden salt and the first 7 bytes of <paramref name="data7"/>.</summary>
    private static uint[] FirstBlock(ReadOnlySpan<byte> data7)
    {
        var w = new uint[64];
        w[14] = (uint)(data7[0] << 16 | data7[1] << 8 | data7[2]);
        w[15] = BinaryPrimitives.ReadUInt32BigEndian(data7[3..7]);
        for (int i = 16; i < 30; i++)
        {
            uint v = ShaSchedule[i - 16];
            unchecked
            {
                if (i - 2 >= 14) v += i == 16 ? SmallSigma1(w[i - 2]) ^ Sigma1Adjustment : SmallSigma1(w[i - 2]);
                if (i - 7 >= 14) v += w[i - 7];
                if (i - 15 >= 14) v += i == 29 ? SmallSigma0(w[i - 15]) ^ Sigma0Adjustment : SmallSigma0(w[i - 15]);
            }
            w[i] = v;
        }
        w[30] = unchecked(SmallSigma1(w[28]) + w[23] + SmallSigma0(w[15]) + w[14] + ShaAdjustment - K[14]);
        Expand(w, 31);
        var working = (uint[])ShaState.Clone();
        Rounds(working, w, 14, KFirstBlock);
        var state = new uint[8];
        for (int i = 0; i < 8; i++) state[i] = unchecked(ShaInitial[i] + working[i]);
        return state;
    }

    private static byte[] Sha256Salted(byte[] data)
    {
        if (data.Length < 7) throw new ArgumentException("the token salt needs seven bytes");
        long length = 57 + data.Length;
        int zeros = (int)(((55 - length) % 64 + 64) % 64);
        var padded = new byte[data.Length - 7 + 1 + zeros + 8];
        data.AsSpan(7).CopyTo(padded);
        padded[data.Length - 7] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), (ulong)(length * 8));
        var state = FirstBlock(data);
        var w = new uint[64];
        for (int off = 0; off < padded.Length; off += 64)
        {
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(off + i * 4));
            Expand(w, 16);
            var working = (uint[])state.Clone();
            Rounds(working, w, 0, K);
            for (int i = 0; i < 8; i++) state[i] = unchecked(state[i] + working[i]);
        }
        var digest = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), state[i]);
        return digest;
    }

    private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    [
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    ];
    private const uint Cs64Factor = 0x82893933, Cs64Adjustment0 = 0xF2F90B1A, Cs64Adjustment1 = 0x0B1A0000;

    private static uint Rol16(uint v) => (v << 16) | (v >> 16);

    private static (uint, uint) Cs64(byte[] data, byte[] digest)
    {
        unchecked
        {
            uint keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1;
            uint keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1;
            uint fA = keyA * Cs64Factor, fB = keyB * Cs64Factor;
            var terms = new List<uint>();
            var offsets = new List<uint>();
            for (int i = 0; i < Cs64Coefficients.Length; i++)
            {
                terms.Add(Cs64Coefficients[i].Coefficient * (i % 2 == 0 ? fA : fB));
                offsets.Add(Cs64Coefficients[i].Offset);
            }
            // Little-endian words of 0x00 + data, zero-padded to an even number of words.
            var bytes = new byte[1 + data.Length + ((4 - (1 + data.Length) % 4) % 4)];
            data.CopyTo(bytes, 1);
            int count = bytes.Length / 4;
            var words = new List<uint>(count + 1);
            for (int i = 0; i < count; i++) words.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)));
            if (words.Count % 2 == 1) words.Add(0);
            for (int i = 0; i < words.Count; i++)
            {
                uint word = words[i];
                uint term = word * (i % 2 == 0 ? keyA : keyB);
                uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
                if (i == 0) { term += Cs64Adjustment0 * fA; offset += Cs64Adjustment1; }
                terms.Add(term);
                offsets.Add(offset);
            }
            uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
            for (int i = 0; i < terms.Count; i += 2)
            {
                uint x = terms[i], y = terms[i + 1];
                uint t = Rol16(offsets[i] + x);
                uint q = t * 0x76F50F34;
                swapAcc += Rol16(q) * 0xC3EFEA97 + q;
                t = Rol16(offsets[i + 1] + y);
                q = t * 0x1881CE3C;
                swapLast = Rol16(q) * 0x35BD1EC9 + q;
                swapAcc += swapLast;

                state = state * keyA + x;
                state = Rol16(state) * 0xCF98B111;
                state = Rol16(state) * 0x87085B9F;
                state = Rol16(state) * 0x12CEB96D;
                state = Rol16(state) * 0x257E1D83;
                acc += state;
                t = state * keyB + y;
                t = Rol16(t) * 0xA27416F5;
                t = Rol16(t) * 0xD38396FF;
                state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
                acc += state;
            }
            return (swapLast ^ state, swapAcc ^ acc);
        }
    }
}

/// <summary>The Edge voice service could not speak this (no network, refused, no audio).</summary>
public sealed class EdgeTtsException(string message, Exception? inner = null) : Exception(message, inner);
