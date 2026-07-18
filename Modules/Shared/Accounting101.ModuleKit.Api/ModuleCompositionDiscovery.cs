using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;

namespace Accounting101.ModuleKit.Api;

/// <summary>Finds every <see cref="IModuleComposition"/> across project-referenced assemblies.
/// Walks the DependencyContext (deps.json) rather than Assembly.GetReferencedAssemblies(): once
/// Program.cs stops naming module types the compiler prunes those assembly references, but
/// ProjectReferences always survive in deps.json as libraries of type "project" — which is also
/// exactly the filter that admits Modules.Private overlays and never NuGet packages.</summary>
public static class ModuleCompositionDiscovery
{
    public static IReadOnlyList<IModuleComposition> DiscoverAll()
    {
        DependencyContext context = DependencyContext.Default
            ?? throw new InvalidOperationException("No DependencyContext — module discovery requires a deps.json.");
        return
        [
            .. context.RuntimeLibraries
                .Where(l => l.Type == "project")
                .OrderBy(l => l.Name, StringComparer.Ordinal)
                .Select(TryLoad)
                .OfType<Assembly>()
                .SelectMany(a => a.GetExportedTypes())
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IModuleComposition).IsAssignableFrom(t))
                .Select(t => (IModuleComposition)Activator.CreateInstance(t)!),
        ];
    }

    /// <summary>A project-type library with no loadable assembly (content-only) is skipped, not fatal.</summary>
    private static Assembly? TryLoad(RuntimeLibrary library)
    {
        try { return Assembly.Load(new AssemblyName(library.Name)); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
    }
}

/// <summary>Host-side composition: discover once, add all, map the same instances later.</summary>
public static class ModuleCompositionHostExtensions
{
    public static void AddDiscoveredModules(this WebApplicationBuilder builder)
    {
        IReadOnlyList<IModuleComposition> modules = ModuleCompositionDiscovery.DiscoverAll();
        foreach (IModuleComposition module in modules)
            module.AddServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton(modules);
    }

    public static void MapDiscoveredModuleEndpoints(this WebApplication app)
    {
        foreach (IModuleComposition module in app.Services.GetRequiredService<IReadOnlyList<IModuleComposition>>())
            module.MapEndpoints(app);
    }
}
