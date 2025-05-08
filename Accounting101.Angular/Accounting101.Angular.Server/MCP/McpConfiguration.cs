using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Accounting101.Angular.DataAccess.Services.Interfaces;

namespace Accounting101.Angular.Server.MCP;

public static class McpConfiguration
{
    public static IServiceCollection AddAccounting101McpServer(this IServiceCollection services)
    {
        Console.WriteLine("========== MCP SERVER CONFIGURATION STARTED ==========");
        
        // Register the AccountService if it's not already registered
        services.TryAddScoped<IAccountService, 
            Accounting101.Angular.DataAccess.Services.AccountService>();
        
        try
        {
            // STEP 1: Log information about assembly and types for debugging
            Console.WriteLine("Checking for MCP tools in assembly...");
            var assembly = Assembly.GetExecutingAssembly();
            Console.WriteLine($"Assembly: {assembly.FullName}");
            
            var toolTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(McpServerToolTypeAttribute), true).Any())
                .ToList();
            
            Console.WriteLine($"Found {toolTypes.Count} types with McpServerToolType attribute:");
            foreach (var type in toolTypes)
            {
                Console.WriteLine($"- {type.FullName}");
                
                // Find methods with McpServerTool attribute
                var methods = type.GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), true).Any())
                    .ToList();
                
                Console.WriteLine($"  Found {methods.Count} methods with McpServerTool attribute:");
                foreach (var method in methods)
                {
                    Console.WriteLine($"  - {method.Name}");
                    
                    // Get parameter information for debugging
                    var parameters = method.GetParameters();
                    Console.WriteLine($"    Parameters: {string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                    
                    // Get return type
                    Console.WriteLine($"    Return type: {method.ReturnType.Name}");
                }
            }
            
            // STEP 2: Create an explicit simple tool class to verify MCP functionality
            services.AddSingleton<SimpleTestTools>();
            
            // STEP 3: Configure MCP server - using the pattern from the official examples
            var mcpServer = services.AddMcpServer()
                .WithHttpTransport();
                
            Console.WriteLine("Adding tools from assembly...");
            mcpServer.WithToolsFromAssembly(assembly);
            Console.WriteLine("Tools from assembly registration completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR during MCP configuration: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        
        Console.WriteLine("========== MCP SERVER CONFIGURATION COMPLETED ==========");
        return services;
    }
    
    public static WebApplication UseMcpServer(this WebApplication app)
    {
        Console.WriteLine("Mapping MCP endpoint to /mcp...");
        
        try
        {
            // Map the MCP endpoint
            app.MapMcp("/mcp");
            Console.WriteLine("MCP endpoint mapped successfully");
            
            // Add debugging middleware to log all requests to MCP endpoint
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/mcp")
                {
                    Console.WriteLine($"Received request to MCP endpoint: {context.Request.Method}");
                    Console.WriteLine("Headers:");
                    foreach (var header in context.Request.Headers)
                    {
                        Console.WriteLine($"- {header.Key}: {string.Join(", ", header.Value)}");
                    }
                    
                    // Enable buffering so we can read the request body
                    context.Request.EnableBuffering();
                    
                    // Read the request body
                    using (var reader = new System.IO.StreamReader(
                        context.Request.Body,
                        encoding: System.Text.Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false,
                        leaveOpen: true))
                    {
                        var body = await reader.ReadToEndAsync();
                        Console.WriteLine($"Request body: {body}");
                        
                        // Reset the request body position
                        context.Request.Body.Position = 0;
                    }
                }
                
                await next();
                
                if (context.Request.Path == "/mcp")
                {
                    Console.WriteLine($"Response status: {context.Response.StatusCode}");
                    
                    // Add response headers to debug output
                    Console.WriteLine("Response headers:");
                    foreach (var header in context.Response.Headers)
                    {
                        Console.WriteLine($"- {header.Key}: {string.Join(", ", header.Value)}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR mapping MCP endpoint: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        
        return app;
    }
}

/// <summary>
/// Simple test tools class created directly with the correct attributes
/// according to the official MCP C# SDK pattern
/// </summary>
[McpServerToolType]
public class SimpleTestTools
{
    private readonly IDataStore _dataStore;
    
    public SimpleTestTools(IDataStore dataStore)
    {
        _dataStore = dataStore;
    }
    
    [McpServerTool, Description("A simple test method")]
    public string TestMethod()
    {
        Console.WriteLine("TestMethod called successfully");
        return "Test method works! This confirms MCP tool registration is functioning.";
    }
    
    [McpServerTool, Description("Get a list of states for US addresses")]
    public async Task<string> GetStates(CancellationToken cancellationToken)
    {
        Console.WriteLine("GetStates called successfully");
        
        var states = await _dataStore.GetStatesAsync();
        
        if (states == null || !states.Any())
        {
            return "No states found.";
        }
        
        return $"Available states:\n{string.Join("\n", states)}";
    }
    
    [McpServerTool, Description("Get a list of countries for foreign addresses")]
    public async Task<string> GetCountries(CancellationToken cancellationToken)
    {
        Console.WriteLine("GetCountries called successfully");
        
        var countries = await _dataStore.GetCountriesAsync();
        
        if (countries == null || !countries.Any())
        {
            return "No countries found.";
        }
        
        return $"Available countries:\n{string.Join("\n", countries)}";
    }

    [McpServerTool, Description("Echoes the message back to the client")]
    public string Echo(string message)
    {
        Console.WriteLine($"Echo called with message: {message}");
        return $"Echo: {message}";
    }
}