using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Banking.Cash.Api;

/// <summary>Discovered by the host (see <see cref="IModuleComposition"/>); delegates to the
/// module's existing extensions.</summary>
public sealed class CashComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddCash(configuration);

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapCashEndpoints();
}
