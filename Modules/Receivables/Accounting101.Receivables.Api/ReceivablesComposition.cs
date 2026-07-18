using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.Receivables.Api;

/// <summary>Discovered by the host (see <see cref="IModuleComposition"/>); delegates to the
/// module's existing extensions.</summary>
public sealed class ReceivablesComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddReceivables(configuration);

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapReceivablesEndpoints();
}
