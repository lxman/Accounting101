using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting101.ModuleKit.Api;

/// <summary>Implemented once per module Api assembly. The host discovers every implementation
/// across its project-referenced assemblies at startup and calls both methods — installing a
/// module means referencing its Api project (the Modules/ list in Host.csproj, or an overlay
/// under Modules.Private/), not naming it in Program.cs. Implementations need a public
/// parameterless constructor — discovery instantiates them reflectively, before DI exists.</summary>
public interface IModuleComposition
{
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder app);
}
