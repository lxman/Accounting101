using System.Security.Claims;
using Accounting101.Ledger.Api.Auth;
using Accounting101.Ledger.Mongo;
using Microsoft.AspNetCore.Http;
using SecurityClaim = System.Security.Claims.Claim;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CurrentActorTests
{
    [Fact]
    public void Derives_the_actor_from_the_current_request_principal()
    {
        Guid userId = Guid.NewGuid();
        DefaultHttpContext ctx = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new SecurityClaim(ClaimTypes.NameIdentifier, userId.ToString()), new SecurityClaim(ClaimTypes.Name, "Alice")],
                "test")),
        };
        IHttpContextAccessor accessor = new HttpContextAccessor { HttpContext = ctx };

        ICurrentActor current = new HttpContextCurrentActor(accessor, new ClaimsActorFactory());
        Actor actor = current.Get();

        Assert.Equal(userId, actor.UserId);
        Assert.Equal("Alice", actor.Name);
    }

    [Fact]
    public void Throws_when_there_is_no_active_request()
    {
        ICurrentActor current = new HttpContextCurrentActor(new HttpContextAccessor(), new ClaimsActorFactory());
        Assert.Throws<InvalidOperationException>(() => current.Get());
    }
}
