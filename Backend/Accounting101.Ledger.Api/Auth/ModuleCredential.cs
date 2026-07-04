namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// The in-process copy of a module's credential: the same key and secret the module sends as
/// <c>X-Module-Key</c> / <c>X-Module-Secret</c> when posting to the engine over HTTP. The
/// <see cref="Secret"/> is populated at startup by <see cref="Hosting.ModuleSecretResolver"/> from the
/// persisted <c>platform_control</c> value — so it is stable across restarts and identical across
/// instances. The module's <c>HttpLedgerClient</c> reads it per request (after startup completes).
/// </summary>
public sealed class ModuleCredential(string key, string secret = "")
{
    public string Key { get; } = key;
    public string Secret { get; set; } = secret;
}
