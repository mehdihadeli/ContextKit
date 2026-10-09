using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace GroundKit.Storage.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroundKitStorage(this IServiceCollection services)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("GROUNDKIT_HOME");
        var rootPath = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.CurrentDirectory, ".groundkit", "packages")
            : Path.Combine(configuredRoot, "packages");

        services.AddSingleton(new PackageStoreOptions(rootPath));
        services.AddSingleton<IPackageStore, SqlitePackageStore>();

        return services;
    }
}
