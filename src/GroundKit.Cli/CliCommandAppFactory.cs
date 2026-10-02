using GroundKit.Mcp;
using GroundKit.Registry;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace GroundKit.Cli;

internal static class CliCommandAppFactory
{
    public static CommandApp Create(IServiceCollection services)
    {
        var app = new CommandApp(new ServiceCollectionTypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("groundkit");

            // A command represents one operation. Its CommandSettings define the positional arguments
            // and options it accepts, such as add <SOURCE> --path <PATH>.
            config
                .AddCommand<AddCliCommand>("add")
                .WithAlias("a")
                .WithDescription("Build and install a documentation package from a source.")
                .WithExample("add", "react")
                .WithExample("a", "./my-library", "-p", "docs", "-n", "my-library");
            config
                .AddCommand<ImportCliCommand>("import")
                .WithAlias("im")
                .WithDescription("Import a portable SQLite package into the local store.")
                .WithExample("import", "./artifacts/react@19.1.0.db");
            config
                .AddCommand<ExportCliCommand>("export")
                .WithAlias("ex")
                .WithAlias("exp")
                .WithDescription("Copy an installed package artifact for sharing.")
                .WithExample("export", "react", "./artifacts");
            config
                .AddCommand<ListCliCommand>("list")
                .WithAlias("l")
                .WithAlias("ls")
                .WithDescription("List installed local documentation packages.")
                .WithExample("ls");
            config
                .AddCommand<InspectCliCommand>("inspect")
                .WithAlias("ins")
                .WithAlias("info")
                .WithDescription("Show package and source metadata.")
                .WithExample("inspect", "react");
            config
                .AddCommand<QueryCliCommand>("query")
                .WithAlias("q")
                .WithDescription(
                    "Search an installed package and return MCP-compatible JSON. Use name@version for an exact version."
                )
                .WithExample("q", "react", "useEffect cleanup");
            config
                .AddCommand<RefreshCliCommand>("refresh")
                .WithAlias("rf")
                .WithAlias("ref")
                .WithDescription("Rebuild an installed package from stored source metadata.")
                .WithExample("refresh", "react");
            config
                .AddCommand<RemoveCliCommand>("remove")
                .WithAlias("rm")
                .WithAlias("del")
                .WithDescription("Remove an installed package from the local store.")
                .WithExample("rm", "react");
            config
                .AddCommand<SearchPackagesCliCommand>("search-packages")
                .WithAlias("sp")
                .WithAlias("search")
                .WithDescription("Search a compatible registry without downloading a package.")
                .WithExample("sp", "npm", "react");
            config
                .AddCommand<DownloadPackageCliCommand>("download-package")
                .WithAlias("dp")
                .WithAlias("dl")
                .WithAlias("download")
                .WithDescription("Download and install one exact package from a registry.")
                .WithExample("dp", "npm", "react", "19.1.0");
            config
                .AddCommand<InstallCliCommand>("install")
                .WithAlias("i")
                .WithDescription("Resolve a package from local store, registry, or source build.")
                .WithExample("install", "react")
                .WithExample("i", "npm/react", "19.1.0");
            config
                .AddCommand<CatalogCommand>("catalog")
                .WithAlias("c")
                .WithAlias("cat")
                .WithDescription("Browse curated starter sources.")
                .WithExample("c", "react");

            // Standalone actions such as add and list accept their own arguments and options.
            // Branches group related commands, so mcp and registry can accept a subcommand plus its arguments.
            config.AddBranch<CommandSettings>("mcp", mcp =>
            {
                mcp.AddCommand<HttpMcpCommand>("http")
                    .WithAlias("h")
                    .WithDescription("Start the HTTP MCP host and expose `/mcp`.");
                mcp.SetDefaultCommand<StdioMcpCommand>();
            });

            RegistryCommandRegistration.Configure(config);
        });
        return app;
    }
}

internal sealed class ServiceCollectionTypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public ITypeResolver Build() =>
        new ServiceCollectionTypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        services.AddSingleton(service, _ => factory());
}

internal sealed class ServiceCollectionTypeResolver(ServiceProvider provider)
    : ITypeResolver,
        IDisposable
{
    public object? Resolve(Type? type) =>
        type is null
            ? null
            : provider.GetService(type) ?? ActivatorUtilities.CreateInstance(provider, type);

    public void Dispose() => provider.Dispose();
}
