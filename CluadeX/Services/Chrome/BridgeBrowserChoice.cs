namespace CluadeX.Services.Chrome;

/// <summary>
/// Which browser the agent's browser tools work in when both Chrome and Edge have the CluadeX Bridge extension. The pipe
/// takes one client, so without a choice it is whichever browser says hello first — and with Chrome open that is always
/// Chrome (owner 2026-11-13, right after Edge support shipped: "จะติดตั้ง edge extension ยังไง … เราต้องไปกดไม่ใช่เหรอ").
/// <para>The choice (<c>AppSettings.BridgeBrowser</c>: "" = the first to connect, "chrome", "edge") is kept, and it is
/// enforced where the browsers arrive: a hello from the other browser is turned away for a short while after the app
/// starts or the owner switches, so the wanted one — whose native host is already waiting on the pipe — gets in. When
/// the wanted browser does not come (it is closed, or the extension is not in it), the other one is let in after the
/// window: the tools keep working, and the Settings row says the wanted browser is not connected.</para>
/// Pure, for the harness.
/// </summary>
public static class BridgeBrowserChoice
{
    public const string Any = "", Chrome = "chrome", Edge = "edge";

    /// <summary>How long after the app starts, or after the owner picks a browser, the other one is turned away.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    /// <summary>At most this many hellos are turned away in one window (a browser that keeps trying is let in).</summary>
    public const int MaxRefusals = 4;

    /// <summary>The setting as one of the three values ("Edge ", "EDGE" → "edge"; anything unknown → the first to connect).</summary>
    public static string Normalize(string? setting) => (setting ?? "").Trim().ToLowerInvariant() switch
    {
        Chrome => Chrome,
        Edge => Edge,
        _ => Any,
    };

    /// <summary>The browser a hello came from, by its user agent: Edge carries "Edg/", every other Chromium counts as Chrome.</summary>
    public static string Of(string? userAgent) => ChromeBridgeConnector.IsEdgeUserAgent(userAgent) ? Edge : Chrome;

    /// <summary>
    /// Let the browser that just said hello in? Yes when no browser was chosen, when it is the chosen one, when the
    /// window is over, or when <see cref="MaxRefusals"/> were already turned away in this window.
    /// </summary>
    public static bool Accept(string wanted, string? userAgent, DateTime nowUtc, DateTime windowEndsUtc, int refusedInWindow) =>
        wanted == Any || Of(userAgent) == wanted || nowUtc >= windowEndsUtc || refusedInWindow >= MaxRefusals;
}
