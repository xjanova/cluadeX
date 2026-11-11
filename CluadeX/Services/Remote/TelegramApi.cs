using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Remote;

/// <summary>The Telegram Bot API, one method call at a time. The real one is <see cref="TelegramHttpApi"/>; tests fake it.</summary>
public interface ITelegramApi
{
    /// <summary>Call <paramref name="method"/> with <paramref name="payload"/> (sent as JSON); the reply's "result".
    /// Throws <see cref="TelegramApiException"/> when Telegram says no, or the network failed.</summary>
    Task<JsonElement> CallAsync(string method, IReadOnlyDictionary<string, object?> payload, CancellationToken ct);
}

/// <summary>What Telegram (or the way there) said went wrong. Never carries the bot token.</summary>
public sealed class TelegramApiException : Exception
{
    /// <summary>Telegram's error_code (401 = the token is wrong, 409 = another program reads this bot), or 0 = no answer.</summary>
    public int Code { get; }
    public TelegramApiException(string message, int code) : base(message) { Code = code; }
}

/// <summary>https://api.telegram.org over HTTPS. The token sits only in the URL of each call, and is scrubbed from
/// every error this class lets out.</summary>
public sealed class TelegramHttpApi : ITelegramApi, IDisposable
{
    private readonly string _token;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(70) };
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public TelegramHttpApi(string token) { _token = token; }

    public async Task<JsonElement> CallAsync(string method, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(payload, Json);
        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await _http.PostAsync($"https://api.telegram.org/bot{_token}/{method}", content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new TelegramApiException("Telegram could not be reached: " + Scrub(ex.Message), 0); }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result))
                    return result.Clone();
                string why = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                int code = root.TryGetProperty("error_code", out var c) && c.TryGetInt32(out int n) ? n : (int)response.StatusCode;
                throw new TelegramApiException(Scrub(why.Length > 0 ? why : $"HTTP {(int)response.StatusCode}"), code);
            }
            catch (JsonException) { throw new TelegramApiException($"Telegram answered HTTP {(int)response.StatusCode} with something that is not JSON", (int)response.StatusCode); }
        }
    }

    private string Scrub(string s) => _token.Length == 0 ? s : s.Replace(_token, "***");

    public void Dispose() => _http.Dispose();
}
