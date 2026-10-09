using Microsoft.Extensions.Logging;

namespace GroundKit.Mcp;

using ModelContextProtocol.Server;

public sealed class GroundKitMcpServer(McpServer server, ILogger<GroundKitMcpServer> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GroundKit MCP stdio host via ModelContextProtocol.");
        await server.RunAsync(cancellationToken);
    }
}
