namespace CluadeX.Services.Account;

/// <summary>
/// The server's error_code (and our own transport codes) → what the user reads (installer contract 2.5, the same table in
/// CluadeX and BrainX). Clients map on the code, never on the server's message text. A code nobody knows reads
/// "เซิร์ฟเวอร์ตอบว่า &lt;code&gt;" and the connection report carries it.
/// </summary>
public static class AccountErrors
{
    /// <summary>The Loc key for a code, or null when the code is unknown.</summary>
    public static string? KeyFor(string? code) => code switch
    {
        "INVALID_CREDENTIALS" => "acct.err.invalidCredentials",
        "ACCOUNT_DISABLED" => "acct.err.accountDisabled",
        "RATE_LIMIT_EXCEEDED" or "TOO_MANY_ATTEMPTS" => "acct.err.rateLimit",
        "EXPIRED_TOKEN" => "acct.err.expiredToken",
        "ACCESS_DENIED" => "acct.err.accessDenied",
        "ALREADY_ACTIVATED_OTHER_DEVICE" => "acct.err.otherDevice",
        "INVALID_LICENSE" => "acct.err.invalidLicense",
        "LICENSE_EXPIRED" => "acct.err.licenseExpired",
        "LICENSE_REVOKED" => "acct.err.licenseRevoked",
        "TRIAL_ALREADY_USED" or "TRIAL_USED_ON_THIS_HARDWARE" => "acct.err.trialUsed",
        "DEVICE_BLOCKED" => "acct.err.deviceBlocked",
        "FREE_LICENSE_NOT_AVAILABLE" => "acct.err.freeNotAvailable",
        "PRODUCT_NOT_FOUND" => "acct.err.productNotFound",
        "VALIDATION_FAILED" => "acct.err.validation",
        "EMPTY_LOGIN" => "acct.err.emptyLogin",
        "EMPTY_PASSWORD" => "acct.err.emptyPassword",
        "EMPTY" => "acct.err.emptyKey",
        "NOT_DEPLOYED" => "acct.err.notDeployed",
        "OFFLINE" => "acct.err.offline",
        "SERVER_ERROR" => "acct.err.serverError",
        "NOT_JSON" => "acct.err.notJson",
        "BAD_RESPONSE" => "acct.err.badResponse",
        _ => null,
    };

    /// <summary>A transport-level failure: the connection doctor should run and its reason be shown.</summary>
    public static bool IsTransport(string? code) => code is "OFFLINE" or "SERVER_ERROR" or "NOT_JSON";

    /// <summary>The sentence for <paramref name="code"/> in the UI's language. Never a raw exception text.</summary>
    public static string Describe(string? code, LocalizationService loc)
    {
        if (string.IsNullOrWhiteSpace(code)) return loc.T("acct.err.generic");
        return KeyFor(code) is { } key ? loc.T(key) : loc.T("acct.err.unknown", code);
    }
}
