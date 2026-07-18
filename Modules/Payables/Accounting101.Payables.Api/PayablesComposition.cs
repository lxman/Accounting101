using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Payables.Api;

/// <summary>Discovered by the host (see <see cref="IModuleComposition"/>); delegates to the
/// module's existing extensions.</summary>
public sealed class PayablesComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddPayables(configuration);

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapPayablesEndpoints();
}
