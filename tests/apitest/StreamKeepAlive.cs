// Keep-alive contract for the agent loop's idle timeout (found by the real Snooker2D run, 2026-10-10):
// while the model writes a large edit into a tool call, the stream carries only input_json_delta events — no text —
// and the loop's 120 s idle window, reset only by text, cut a healthy turn off ("Request timed out after 120s").
// Every streaming provider now calls onTextDelta("") for each stream event; the loop resets on any call and only
// forwards non-empty tokens to the UI. These checks drive the REAL providers against recorded/mocked streams.
using System.Net.Http;
using System.Text;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ApiTest;

internal static class StreamKeepAlive
{
    // A tool call whose input arrives in 6 JSON fragments and NO text at all.
    const string ToolOnlySse =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"echo\",\"input\":{}}}\n\n" +
        "event: ping\ndata: {\"type\":\"ping\"}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"te\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"xt\\\":\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\" \\\"a long\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\" edit\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\" body\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"}\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":30}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · stream keep-alive for the agent loop's idle timeout");
        var svc = new SettingsService();   // portable — never the user's settings

        using (var mock = new GoldenMock())
        {
            check("keepalive", "Anthropic mock proves its marker before any request", await mock.ProveAsync(), mock.Root);
            mock.Responder = _ => (200, "text/event-stream", ToolOnlySse);
            ImageWireGolden.Pin(svc.Settings, "claude-opus-5-5", mock.Root);
            var p = new AnthropicProvider(svc);
            int empty = 0; var text = new StringBuilder();
            var r = await p.ChatWithToolsAsync(ImageWireGolden.Conversation(), "sys", ImageWireGolden.Tools(),
                onTextDelta: t => { if (t.Length == 0) empty++; else text.Append(t); });
            check("keepalive", "Anthropic: a tool-only stream (no text) still signals activity on every event (≥ 6 keep-alives)",
                empty >= 6, $"keep-alives={empty}");
            check("keepalive", "control: keep-alives add no text (the reply stays empty) and the tool call is intact",
                text.Length == 0 && r.ToolCalls.Count == 1 && r.ToolCalls[0].Name == "echo", $"text='{text}' calls={r.ToolCalls.Count}");
        }

        // OpenAI Responses + Gemini parsers are public statics: feed them function-call-only streams.
        const string oa =
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"c1\",\"name\":\"echo\",\"arguments\":\"\"}}\n\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":0,\"delta\":\"{\\\"text\\\":\"}\n\n" +
            "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":0,\"delta\":\"\\\"x\\\"}\"}\n\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"c1\",\"name\":\"echo\",\"arguments\":\"{\\\"text\\\":\\\"x\\\"}\"}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}}\n\n";
        int oaEmpty = 0; var oaText = new StringBuilder();
        await OpenAiProvider.ParseResponsesStreamAsync(new StringContent(oa, Encoding.UTF8, "text/event-stream"),
            t => { if (t.Length == 0) oaEmpty++; else oaText.Append(t); }, CancellationToken.None);
        check("keepalive", "OpenAI Responses: function-call-only stream signals activity per event, adds no text",
            oaEmpty >= 5 && oaText.Length == 0, $"keep-alives={oaEmpty} text='{oaText}'");

        const string gm =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"plan\",\"thought\":true}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"id\":\"f\",\"name\":\"echo\",\"args\":{\"text\":\"x\"}}}]},\"finishReason\":\"STOP\"}]}\n\n";
        int gmEmpty = 0; var gmText = new StringBuilder();
        await GeminiProvider.ParseToolStreamAsync(new StringContent(gm, Encoding.UTF8, "text/event-stream"),
            t => { if (t.Length == 0) gmEmpty++; else gmText.Append(t); }, CancellationToken.None);
        check("keepalive", "Gemini: thought + function-call stream signals activity per event, adds no text",
            gmEmpty >= 2 && gmText.Length == 0, $"keep-alives={gmEmpty} text='{gmText}'");
    }
}
