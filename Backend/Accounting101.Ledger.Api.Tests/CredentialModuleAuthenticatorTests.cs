using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Api.Control;
using Microsoft.AspNetCore.Http;

namespace Accounting101.Ledger.Api.Tests;

/// <summary>
/// The credential-verifying authenticator reads X-Module-Key + X-Module-Secret from the current
/// HTTP request, looks up the registration in the control store, and compares the secret in
/// constant time. It is the out-of-process twin of HostStampedModuleAuthenticator: same seam,
/// different trust model.
/// </summary>
public sealed class CredentialModuleAuthenticatorTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static IHttpContextAccessor AccessorWith(string? key, string? secret)
    {
        DefaultHttpContext ctx = new();
        if (key is not null)
            ctx.Request.Headers["X-Module-Key"] = key;
        if (secret is not null)
            ctx.Request.Headers["X-Module-Secret"] = secret;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static async Task<ControlStore> StoreWithModule(
        ApiFixture fixture, string key, string secret, bool enabled = true)
    {
        ControlStore store = fixture.Control();
        await store.RegisterModuleAsync(new ModuleRegistration
        {
            Key = key,
            Name = key,
            Enabled = enabled,
            Secret = secret,
        });
        return store;
    }

    [Fact]
    public async Task Valid_key_and_matching_secret_returns_identity_with_correct_key()
    {
        ControlStore store = await StoreWithModule(fixture, "payables", "correct-secret");
        IHttpContextAccessor accessor = AccessorWith("payables", "correct-secret");
        CredentialModuleAuthenticator sut = new(accessor, store);

        ModuleIdentity? identity = await sut.AuthenticateAsync();

        Assert.NotNull(identity);
        Assert.Equal("payables", identity!.Key);
    }

    [Fact]
    public async Task Wrong_secret_returns_null()
    {
        ControlStore store = await StoreWithModule(fixture, "payables-wrong", "correct-secret");
        IHttpContextAccessor accessor = AccessorWith("payables-wrong", "wrong-secret");
        CredentialModuleAuthenticator sut = new(accessor, store);

        ModuleIdentity? identity = await sut.AuthenticateAsync();

        Assert.Null(identity);
    }

    [Fact]
    public async Task Absent_headers_returns_null()
    {
        ControlStore store = fixture.Control();
        IHttpContextAccessor accessor = AccessorWith(key: null, secret: null);
        CredentialModuleAuthenticator sut = new(accessor, store);

        ModuleIdentity? identity = await sut.AuthenticateAsync();

        Assert.Null(identity);
    }

    [Fact]
    public async Task Unknown_key_returns_null()
    {
        ControlStore store = fixture.Control();
        IHttpContextAccessor accessor = AccessorWith("no-such-module", "any-secret");
        CredentialModuleAuthenticator sut = new(accessor, store);

        ModuleIdentity? identity = await sut.AuthenticateAsync();

        Assert.Null(identity);
    }

    /// <summary>
    /// A disabled module authenticates successfully — the enabled flag is a gateway concern (Task 3),
    /// not an authentication concern. Authentication proves identity; authorization decides access.
    /// Separating the two keeps the authenticator a pure identity check and avoids hard-coupling
    /// the auth layer to gateway policy.
    /// </summary>
    [Fact]
    public async Task Disabled_module_authenticates_successfully_returns_identity()
    {
        ControlStore store = await StoreWithModule(fixture, "payables-disabled", "secret", enabled: false);
        IHttpContextAccessor accessor = AccessorWith("payables-disabled", "secret");
        CredentialModuleAuthenticator sut = new(accessor, store);

        ModuleIdentity? identity = await sut.AuthenticateAsync();

        Assert.NotNull(identity);
        Assert.Equal("payables-disabled", identity!.Key);
    }
}
