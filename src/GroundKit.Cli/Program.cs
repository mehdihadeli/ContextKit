using GroundKit.Cli;
using GroundKit.Hosting;
using GroundKit.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var verbose = args.Any(argument =>
    string.Equals(argument, "--verbose", StringComparison.OrdinalIgnoreCase)
);
args = NormalizeCommandArguments(args);
args = args.Where(argument =>
        !string.Equals(argument, "--verbose", StringComparison.OrdinalIgnoreCase)
    )
    .ToArray();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Error);
builder.Services.AddGroundKitServices();
builder.Services.AddSingleton<RegistryApplication>();
builder.Services.AddHttpClient<RegistryPublisher>(client =>
{
    var baseUrl =
        Environment.GetEnvironmentVariable("REGISTRY_SERVER_URL")?.TrimEnd('/')
        ?? "http://localhost:8080";
    client.BaseAddress = new Uri(baseUrl + "/");
});
builder.Services.AddSingleton<CliApplication>();

var app = CliCommandAppFactory.Create(builder.Services);
return await app.RunAsync(args);

static string[] NormalizeCommandArguments(string[] arguments)
{
    if (
        arguments.Length < 2
        || !string.Equals(arguments[0], "mcp", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(arguments[1], "--http", StringComparison.OrdinalIgnoreCase)
    )
    {
        return arguments;
    }

    var normalized = new List<string> { "mcp", "http" };
    var index = 2;
    if (index < arguments.Length && int.TryParse(arguments[index], out _))
    {
        normalized.Add("--port");
        normalized.Add(arguments[index]);
        index++;
    }

    normalized.AddRange(arguments[index..]);
    return [.. normalized];
}
