#include "claude_api.h"
#include <windows.h>
#include <winhttp.h>
#include <sstream>

#pragma comment(lib, "winhttp.lib")

// ── UTF Conversion ──────────────────────────────────────────────────

std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    int sz = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string out(sz, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &out[0], sz, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return {};
    int sz = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring out(sz, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], sz);
    return out;
}

// ── Simple JSON helpers ─────────────────────────────────────────────

static std::string EscapeJsonUtf8(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 32);
    for (char c : s) {
        switch (c) {
        case '\\': out += "\\\\"; break;
        case '"':  out += "\\\""; break;
        case '\n': out += "\\n";  break;
        case '\r': out += "\\r";  break;
        case '\t': out += "\\t";  break;
        default:
            if ((unsigned char)c < 0x20) {
                char buf[8];
                snprintf(buf, sizeof(buf), "\\u%04x", (unsigned char)c);
                out += buf;
            } else {
                out += c;
            }
            break;
        }
    }
    return out;
}

static std::string ExtractJsonString(const std::string& json, const std::string& key) {
    std::string search = "\"" + key + "\"";
    size_t pos = json.find(search);
    if (pos == std::string::npos) return "";

    pos = json.find(':', pos + search.size());
    if (pos == std::string::npos) return "";

    // Skip whitespace
    size_t valStart = pos + 1;
    while (valStart < json.size() && (json[valStart] == ' ' || json[valStart] == '\t' || json[valStart] == '\n' || json[valStart] == '\r'))
        valStart++;

    if (valStart >= json.size() || json[valStart] != '"') return "";
    valStart++;

    std::string value;
    while (valStart < json.size() && json[valStart] != '"') {
        if (json[valStart] == '\\' && valStart + 1 < json.size()) {
            switch (json[valStart + 1]) {
            case '\\': value += '\\'; break;
            case '"':  value += '"';  break;
            case 'n':  value += '\n'; break;
            case 'r':  value += '\r'; break;
            case 't':  value += '\t'; break;
            case '/':  value += '/';  break;
            case 'u': {
                if (valStart + 5 < json.size()) {
                    std::string hex = json.substr(valStart + 2, 4);
                    unsigned int cp = (unsigned int)strtoul(hex.c_str(), nullptr, 16);
                    if (cp < 0x80) {
                        value += (char)cp;
                    } else if (cp < 0x800) {
                        value += (char)(0xC0 | (cp >> 6));
                        value += (char)(0x80 | (cp & 0x3F));
                    } else {
                        value += (char)(0xE0 | (cp >> 12));
                        value += (char)(0x80 | ((cp >> 6) & 0x3F));
                        value += (char)(0x80 | (cp & 0x3F));
                    }
                    valStart += 5;
                }
                break;
            }
            default: value += json[valStart + 1]; break;
            }
            valStart += 2;
        } else {
            value += json[valStart++];
        }
    }
    return value;
}

static int ExtractJsonInt(const std::string& json, const std::string& key, int def = 0) {
    std::string search = "\"" + key + "\"";
    size_t pos = json.find(search);
    if (pos == std::string::npos) return def;
    pos = json.find(':', pos + search.size());
    if (pos == std::string::npos) return def;
    pos++;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t')) pos++;
    std::string num;
    while (pos < json.size() && (isdigit(json[pos]) || json[pos] == '-'))
        num += json[pos++];
    return num.empty() ? def : atoi(num.c_str());
}

// Extract text from Claude API response
// {"content":[{"type":"text","text":"..."}], "usage":{...}}
static std::string ExtractClaudeResponseText(const std::string& json) {
    size_t contentPos = json.find("\"content\"");
    if (contentPos == std::string::npos) return "";
    size_t typePos = json.find("\"type\"", contentPos);
    if (typePos == std::string::npos) return "";
    size_t textPos = json.find("\"text\"", typePos + 6);
    if (textPos == std::string::npos) return "";

    size_t colon = json.find(':', textPos + 6);
    if (colon == std::string::npos) return "";
    size_t quote = json.find('"', colon + 1);
    if (quote == std::string::npos) return "";
    quote++;

    std::string value;
    while (quote < json.size() && json[quote] != '"') {
        if (json[quote] == '\\' && quote + 1 < json.size()) {
            switch (json[quote + 1]) {
            case '\\': value += '\\'; break;
            case '"':  value += '"';  break;
            case 'n':  value += '\n'; break;
            case 'r':  value += '\r'; break;
            case 't':  value += '\t'; break;
            case '/':  value += '/';  break;
            case 'u': {
                if (quote + 5 < json.size()) {
                    std::string hex = json.substr(quote + 2, 4);
                    unsigned int cp = (unsigned int)strtoul(hex.c_str(), nullptr, 16);
                    if (cp < 0x80) value += (char)cp;
                    else if (cp < 0x800) {
                        value += (char)(0xC0 | (cp >> 6));
                        value += (char)(0x80 | (cp & 0x3F));
                    } else {
                        value += (char)(0xE0 | (cp >> 12));
                        value += (char)(0x80 | ((cp >> 6) & 0x3F));
                        value += (char)(0x80 | (cp & 0x3F));
                    }
                    quote += 5;
                }
                break;
            }
            default: value += json[quote + 1]; break;
            }
            quote += 2;
        } else {
            value += json[quote++];
        }
    }
    return value;
}

// ── Generic WinHTTP Request ─────────────────────────────────────────

struct HttpResult {
    DWORD       statusCode = 0;
    std::string body;
    std::string error;
};

// Parse URL into host, port, path, and whether it's HTTPS
static bool ParseUrl(const std::wstring& url,
    std::wstring& host, INTERNET_PORT& port, std::wstring& path, bool& useSSL)
{
    useSSL = false;
    std::wstring work = url;

    if (work.substr(0, 8) == L"https://") {
        useSSL = true;
        work = work.substr(8);
    } else if (work.substr(0, 7) == L"http://") {
        work = work.substr(7);
    }

    // Split host:port and path
    size_t slashPos = work.find(L'/');
    std::wstring hostPort = (slashPos != std::wstring::npos) ? work.substr(0, slashPos) : work;
    path = (slashPos != std::wstring::npos) ? work.substr(slashPos) : L"/";

    size_t colonPos = hostPort.find(L':');
    if (colonPos != std::wstring::npos) {
        host = hostPort.substr(0, colonPos);
        port = (INTERNET_PORT)_wtoi(hostPort.substr(colonPos + 1).c_str());
    } else {
        host = hostPort;
        port = useSSL ? INTERNET_DEFAULT_HTTPS_PORT : INTERNET_DEFAULT_HTTP_PORT;
    }
    return !host.empty();
}

static HttpResult DoHttpRequest(const std::wstring& method,
    const std::wstring& fullUrl, const std::string& bodyStr,
    const std::wstring& extraHeaders = L"")
{
    HttpResult hr;

    std::wstring host, path;
    INTERNET_PORT port;
    bool useSSL;
    if (!ParseUrl(fullUrl, host, port, path, useSSL)) {
        hr.error = "Invalid URL";
        return hr;
    }

    HINTERNET hSession = WinHttpOpen(L"CluadeX/1.0",
        WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hSession) { hr.error = "WinHttpOpen failed"; return hr; }

    HINTERNET hConnect = WinHttpConnect(hSession, host.c_str(), port, 0);
    if (!hConnect) { hr.error = "WinHttpConnect failed"; WinHttpCloseHandle(hSession); return hr; }

    DWORD flags = useSSL ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET hRequest = WinHttpOpenRequest(hConnect, method.c_str(), path.c_str(),
        nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!hRequest) {
        hr.error = "WinHttpOpenRequest failed";
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return hr;
    }

    DWORD timeout = 120000;
    WinHttpSetTimeouts(hRequest, timeout, timeout, timeout, timeout);

    std::wstring headers = L"Content-Type: application/json\r\n" + extraHeaders;

    BOOL ok = WinHttpSendRequest(hRequest,
        headers.c_str(), (DWORD)headers.size(),
        bodyStr.empty() ? WINHTTP_NO_REQUEST_DATA : (LPVOID)bodyStr.c_str(),
        (DWORD)bodyStr.size(), (DWORD)bodyStr.size(), 0);

    if (!ok) {
        hr.error = "WinHttpSendRequest failed (error " + std::to_string(GetLastError()) + ")";
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return hr;
    }

    ok = WinHttpReceiveResponse(hRequest, nullptr);
    if (!ok) {
        hr.error = "WinHttpReceiveResponse failed";
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return hr;
    }

    DWORD statusSize = sizeof(hr.statusCode);
    WinHttpQueryHeaders(hRequest,
        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX, &hr.statusCode, &statusSize,
        WINHTTP_NO_HEADER_INDEX);

    DWORD bytesAvailable = 0;
    do {
        bytesAvailable = 0;
        WinHttpQueryDataAvailable(hRequest, &bytesAvailable);
        if (bytesAvailable > 0) {
            std::vector<char> buf(bytesAvailable + 1, 0);
            DWORD bytesRead = 0;
            WinHttpReadData(hRequest, buf.data(), bytesAvailable, &bytesRead);
            hr.body.append(buf.data(), bytesRead);
        }
    } while (bytesAvailable > 0);

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return hr;
}

// ── Cloud API (Anthropic) ───────────────────────────────────────────

ApiResponse ClaudeAPI::SendChat(
    const std::wstring& apiKey,
    const std::wstring& model,
    int maxTokens,
    double temperature,
    const std::string& systemPrompt,
    const std::vector<ChatMessage>& messages)
{
    ApiResponse resp;

    std::ostringstream body;
    body << "{";
    body << "\"model\":\"" << WideToUtf8(model) << "\",";
    body << "\"max_tokens\":" << maxTokens << ",";
    if (temperature >= 0.0 && temperature <= 2.0)
        body << "\"temperature\":" << temperature << ",";
    if (!systemPrompt.empty())
        body << "\"system\":\"" << EscapeJsonUtf8(systemPrompt) << "\",";

    body << "\"messages\":[";
    for (size_t i = 0; i < messages.size(); i++) {
        if (i > 0) body << ",";
        body << "{\"role\":\"" << messages[i].role << "\",";
        body << "\"content\":\"" << EscapeJsonUtf8(messages[i].content) << "\"}";
    }
    body << "]}";

    std::wstring headers;
    headers += L"x-api-key: " + apiKey + L"\r\n";
    headers += L"anthropic-version: 2023-06-01\r\n";

    HttpResult hr = DoHttpRequest(L"POST",
        L"https://api.anthropic.com/v1/messages", body.str(), headers);

    if (!hr.error.empty()) { resp.error = hr.error; return resp; }

    if (hr.statusCode == 200) {
        resp.success = true;
        resp.text = ExtractClaudeResponseText(hr.body);
        resp.inputTokens = ExtractJsonInt(hr.body, "input_tokens");
        resp.outputTokens = ExtractJsonInt(hr.body, "output_tokens");
        if (resp.text.empty()) resp.text = "(Empty response)";
    } else {
        std::string errMsg = ExtractJsonString(hr.body, "message");
        if (errMsg.empty()) errMsg = hr.body.substr(0, 500);
        resp.error = "HTTP " + std::to_string(hr.statusCode) + ": " + errMsg;
    }
    return resp;
}

ApiResponse ClaudeAPI::TestConnection(const std::wstring& apiKey, const std::wstring& model) {
    std::vector<ChatMessage> msgs = { {"user", "Say hello in one word."} };
    return SendChat(apiKey, model, 32, 1.0, "", msgs);
}

// ── Local API (Ollama — OpenAI-compatible /v1/chat/completions) ─────

ApiResponse ClaudeAPI::SendChatLocal(
    const std::wstring& endpoint,
    const std::wstring& model,
    int maxTokens,
    double temperature,
    const std::string& systemPrompt,
    const std::vector<ChatMessage>& messages)
{
    ApiResponse resp;

    // Ollama uses OpenAI-compatible chat format
    // POST <endpoint>/v1/chat/completions
    std::ostringstream body;
    body << "{";
    body << "\"model\":\"" << WideToUtf8(model) << "\",";
    body << "\"max_tokens\":" << maxTokens << ",";
    body << "\"temperature\":" << temperature << ",";
    body << "\"stream\":false,";

    body << "\"messages\":[";

    // System prompt as first message
    bool first = true;
    if (!systemPrompt.empty()) {
        body << "{\"role\":\"system\",\"content\":\"" << EscapeJsonUtf8(systemPrompt) << "\"}";
        first = false;
    }

    for (size_t i = 0; i < messages.size(); i++) {
        if (!first) body << ",";
        body << "{\"role\":\"" << messages[i].role << "\",";
        body << "\"content\":\"" << EscapeJsonUtf8(messages[i].content) << "\"}";
        first = false;
    }
    body << "]}";

    std::wstring url = endpoint + L"/v1/chat/completions";
    HttpResult hr = DoHttpRequest(L"POST", url, body.str());

    if (!hr.error.empty()) {
        // Try Ollama native API as fallback
        std::ostringstream body2;
        body2 << "{";
        body2 << "\"model\":\"" << WideToUtf8(model) << "\",";
        body2 << "\"stream\":false,";
        body2 << "\"options\":{\"num_predict\":" << maxTokens
              << ",\"temperature\":" << temperature << "},";

        body2 << "\"messages\":[";
        first = true;
        if (!systemPrompt.empty()) {
            body2 << "{\"role\":\"system\",\"content\":\"" << EscapeJsonUtf8(systemPrompt) << "\"}";
            first = false;
        }
        for (size_t i = 0; i < messages.size(); i++) {
            if (!first) body2 << ",";
            body2 << "{\"role\":\"" << messages[i].role << "\",";
            body2 << "\"content\":\"" << EscapeJsonUtf8(messages[i].content) << "\"}";
            first = false;
        }
        body2 << "]}";

        std::wstring url2 = endpoint + L"/api/chat";
        hr = DoHttpRequest(L"POST", url2, body2.str());
    }

    if (!hr.error.empty()) { resp.error = hr.error; return resp; }

    if (hr.statusCode == 200) {
        resp.success = true;

        // Try OpenAI format: choices[0].message.content
        std::string text = ExtractJsonString(hr.body, "content");

        // If that gets the wrong "content", look for "message" -> "content"
        if (text.empty() || text.find("[{") == 0) {
            // Ollama native: {"message":{"role":"assistant","content":"..."}}
            size_t msgPos = hr.body.find("\"message\"");
            if (msgPos != std::string::npos) {
                size_t contentPos = hr.body.find("\"content\"", msgPos);
                if (contentPos != std::string::npos) {
                    // Extract from that position
                    std::string sub = hr.body.substr(contentPos);
                    text = ExtractJsonString(sub, "content");
                }
            }
        }

        // Try choices[0].message.content (OpenAI format)
        if (text.empty()) {
            size_t choicesPos = hr.body.find("\"choices\"");
            if (choicesPos != std::string::npos) {
                size_t msgPos = hr.body.find("\"message\"", choicesPos);
                if (msgPos != std::string::npos) {
                    size_t contentPos = hr.body.find("\"content\"", msgPos);
                    if (contentPos != std::string::npos) {
                        std::string sub = hr.body.substr(contentPos);
                        text = ExtractJsonString(sub, "content");
                    }
                }
            }
        }

        resp.text = text.empty() ? "(Empty response from local model)" : text;

        resp.inputTokens = ExtractJsonInt(hr.body, "prompt_eval_count");
        resp.outputTokens = ExtractJsonInt(hr.body, "eval_count");
        // OpenAI format fallback
        if (resp.inputTokens == 0)
            resp.inputTokens = ExtractJsonInt(hr.body, "prompt_tokens");
        if (resp.outputTokens == 0)
            resp.outputTokens = ExtractJsonInt(hr.body, "completion_tokens");

    } else {
        std::string errMsg = ExtractJsonString(hr.body, "error");
        if (errMsg.empty()) errMsg = hr.body.substr(0, 500);
        resp.error = "HTTP " + std::to_string(hr.statusCode) + ": " + errMsg;
    }
    return resp;
}

ApiResponse ClaudeAPI::TestConnectionLocal(const std::wstring& endpoint, const std::wstring& model) {
    std::vector<ChatMessage> msgs = { {"user", "Say hello in one word."} };
    return SendChatLocal(endpoint, model, 32, 0.7, "", msgs);
}

// ── List Ollama Models ──────────────────────────────────────────────

std::vector<std::string> ClaudeAPI::ListLocalModels(const std::wstring& endpoint) {
    std::vector<std::string> models;

    std::wstring url = endpoint + L"/api/tags";
    HttpResult hr = DoHttpRequest(L"GET", url, "");

    if (hr.statusCode != 200 || hr.body.empty()) return models;

    // Parse {"models":[{"name":"llama3.2:3b",...}, ...]}
    // Simple extraction: find all "name":"..." patterns after "models"
    size_t modelsPos = hr.body.find("\"models\"");
    if (modelsPos == std::string::npos) return models;

    size_t pos = modelsPos;
    while (true) {
        pos = hr.body.find("\"name\"", pos + 1);
        if (pos == std::string::npos) break;

        std::string name = ExtractJsonString(hr.body.substr(pos), "name");
        if (!name.empty()) {
            models.push_back(name);
        }
        pos += 6;
    }

    return models;
}
