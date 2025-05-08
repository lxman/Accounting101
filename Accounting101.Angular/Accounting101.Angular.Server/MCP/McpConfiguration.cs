using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol.Types;

namespace Accounting101.Angular.Server.MCP;

public static class McpConfiguration
{
    public static IServiceCollection AddMcpServer(this IServiceCollection services)
    {
        // Register the AccountService if it's not already registered
        services.TryAddScoped<Accounting101.Angular.DataAccess.Services.Interfaces.IAccountService, 
            Accounting101.Angular.DataAccess.Services.AccountService>();
            
        // Register MCP server with AspNetCore extensions
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation 
                { 
                    Name = "Accounting101MCP", 
                    Version = "1.0.0" 
                };
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();
        
        return services;
    }
    
    public static WebApplication UseMcpServer(this WebApplication app)
    {
        // Use the AspNetCore extensions to map the MCP endpoint
        app.MapMcp("/mcp");
        
        return app;
    }
}