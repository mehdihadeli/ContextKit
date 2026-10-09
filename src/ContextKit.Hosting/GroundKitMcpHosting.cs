using GroundKit.Mcp;
using GroundKit.Mcp.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GroundKit.Hosting;

public static class GroundKitMcpHosting
{
    public static async Task<int> RunStdioAsync(string? libraries, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(libraries))
            Environment.SetEnvironmentVariable("GROUNDKIT_ALLOWED_LIBRARIES", libraries);
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddGroundKitServices();
        builder.Services.AddGroundKitMcp(useHttpTransport: false);
        using var host = builder.Build();
        await host.Services.GetRequiredService<GroundKitMcpServer>().RunAsync(cancellationToken);
        return 0;
    }

    public static async Task<int> RunHttpAsync(string? libraries, string? urls, string? host, int? port,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(libraries))
            Environment.SetEnvironmentVariable("GROUNDKIT_ALLOWED_LIBRARIES", libraries);
        await using var app = CreateHttpApp(libraries, urls, host, port);
        await app.RunAsync(cancellationToken);
        return 0;
    }

    public static WebApplication CreateHttpApp(string? libraries, string? urls, string? host, int? port)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.AddServiceDefaults();
        builder.Services.AddGroundKitServices();
        builder.Services.AddGroundKitMcp(useStdioTransport: false);
        builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(urls)
            ? $"http://{(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host)}:{port ?? 4000}"
            : urls);
        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapMcp("/mcp");
        return app;
    }
}