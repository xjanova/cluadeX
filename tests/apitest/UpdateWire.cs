// The store's update check (2026-10-13): CluadeX posted to /api/v1/products/cluadex/check-update — a slug the store does
// not have, so every check was a 404 and fell through to GitHub — and read a flat "update_available" shape the store
// never sends. The fixture below is the store's real reply that day (changelog shortened).
using CluadeX.Services;

namespace ApiTest;

internal static class UpdateWire
{
    const string StoreReply = """
        {"success":true,"current_version":"3.0.0","latest_version":"3.0.54","has_update":true,"coming_soon":false,
         "update":{"version":"3.0.54","filename":"CluadeX-win-Portable.zip","file_size":477424210,"file_size_formatted":"455.31 MB",
         "changelog":"## CluadeX v3.0.54\n\nAI Coding Assistant ","released_at":"2026-10-13T06:40:05+00:00",
         "download_url":"https://xman4289.com/download/cluadex-ai-coding-assistant/3.0.54",
         "sha256":"24d8dc5197c2bf4aa81844fa542680cbedf5290f5d8d99357e494e54c9e8b835"},
         "license":{"valid":false,"status":null,"can_download":false}}
        """;

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · the store's update check (xman4289.com)");
        var u = AutoUpdateService.ParseXmanCheck(StoreReply, "3.0.0");
        check("update", "the store's real reply is read: version, file, size, sha256, a download link on xman4289.com",
            u != null && u.NewVersion == "3.0.54" && u.FileName == "CluadeX-win-Portable.zip" && u.FileSize == 477424210
            && u.Sha256.StartsWith("24d8dc51") && u.DownloadUrl.StartsWith("https://xman4289.com/"), u == null ? "null" : $"{u.NewVersion} {u.FileName} {u.DownloadUrl}");
        var none = AutoUpdateService.ParseXmanCheck("""{"success":true,"latest_version":"3.0.54","has_update":false,"update":null}""", "3.0.54");
        check("update", "has_update false → no update", none == null, "");
        var old = AutoUpdateService.ParseXmanCheck("""{"update_available":true,"version":"9.9.9","download_url":"https://xman4289.com/x","download_filename":"a.zip"}""", "1.0.0");
        check("update", "the old flat shape is still read", old is { NewVersion: "9.9.9", FileName: "a.zip" }, old?.NewVersion ?? "null");
        var notFound = AutoUpdateService.ParseXmanCheck("""{"success":false,"error":"Product not found"}""", "1.0.0");
        check("update", "the store's 404 body → no update (and the caller falls back to GitHub)", notFound == null, "");
        return Task.CompletedTask;
    }
}
