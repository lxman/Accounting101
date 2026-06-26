namespace Accounting101.Ledger.Api.Auth;

/// <summary>
/// The in-process copy of a module's credential: the same key and secret that the module sends as
/// <c>X-Module-Key</c> / <c>X-Module-Secret</c> headers when posting to the engine over HTTP.
/// Registered in the module's DI scope by <see cref="Hosting.ModuleHostingExtensions.AddModule"/>
/// so the module's <c>HttpLedgerClient</c> (Task 4) can inject it and attach the headers.
/// </summary>
public sealed record ModuleCredential(string Key, string Secret);
