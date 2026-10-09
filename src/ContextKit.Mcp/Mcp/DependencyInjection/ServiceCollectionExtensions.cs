using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace GroundKit.Mcp.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroundKitMcp(
        this IServiceCollection services,
        bool useStdioTransport = true,
        bool useHttpTransport = true
    )
    {
        services.AddSingleton<GroundKitToolResponseAgent>();
        var serverBuilder = services.AddMcpServer();
        if (useStdioTransport)
        {
            serverBuilder = serverBuilder.WithStdioServerTransport();
        }

        if (useHttpTransport)
        {
            serverBuilder = serverBuilder.WithHttpTransport();
        }

        serverBuilder.WithToolsFromAssembly();
        services.AddSingleton<GroundKitMcpServer>();
        return services;
    }
}
