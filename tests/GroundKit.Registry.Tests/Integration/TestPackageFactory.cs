using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// Writes a real SQLite package so registry tests exercise genuine manifests instead of stubs.
/// </summary>
internal static class TestPackageFactory
{
    public static void Create(
        string sourceRoot,
        string outputDirectory,
        string packageId,
        string version
    )
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        var packageBuilder = provider.GetRequiredService<IDocumentPackageBuilder>();
        var source = Path.Combine(sourceRoot, "sources", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "guide.md"),
            $"# {packageId}\n\n{packageId} keeps this document."
        );

        try
        {
            var result = packageBuilder
                .BuildAsync(source, null, TestContext.Current.CancellationToken, version, null)
                .GetAwaiter()
                .GetResult();
            var normalized = result with
            {
                Source = result.Source with { CanonicalId = packageId, DisplayName = packageId },
                Manifest = result.Manifest with
                {
                    PackageId = packageId,
                    DisplayName = packageId,
                    Version = version,
                    SourceCanonicalId = packageId,
                },
            };
            new SqlitePackageStore(
                new PackageStoreOptions(outputDirectory),
                NullLogger<SqlitePackageStore>.Instance
            )
                .SaveAsync(normalized)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }

        File.Exists(Path.Combine(outputDirectory, $"{packageId}@{version}.db")).ShouldBeTrue();
    }
}
