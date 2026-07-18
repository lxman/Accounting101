using Accounting101.ModuleKit.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.FixedAssets.Api;

/// <summary>Discovered by the host (see <see cref="IModuleComposition"/>); delegates to the
/// module's existing extensions.</summary>
public sealed class FixedAssetsComposition : IModuleComposition
{
    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddFixedAssets(configuration);

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapFixedAssetsEndpoints();
}
