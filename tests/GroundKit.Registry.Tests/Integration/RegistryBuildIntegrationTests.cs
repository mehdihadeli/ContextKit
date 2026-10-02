using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Hosting;
using GroundKit.Configuration;
using GroundKit.Ingestion.Services;
using GroundKit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Registry.Tests.Integration;

public sealed class RegistryBuildIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-integration",
        Guid.NewGuid().ToString("n")
    );

    public RegistryBuildIntegrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Should_Validate_Checked_In_Npm_Catalog()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registryPath = Path.Combine(repositoryRoot, "registry");
        var definitions = RegistryDefinitionLoader.LoadDirectory(registryPath);

        definitions.Count.ShouldBe(20);
        definitions.ShouldAllBe(definition => definition.Registry == "npm");
        definitions.ShouldAllBe(definition => definition.Kind == SourceKind.GitRepository);
        definitions.ShouldAllBe(definition => definition.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        definitions.ShouldAllBe(definition => !string.IsNullOrWhiteSpace(definition.DocsPath));

        var exitCode = await new RegistryApplication(
                CreatePackageBuilder(),
                NullLoggerFactory.Instance,
                new RegistryPublisher(new HttpClient()),
                new TestHttpClientFactory(new RecordingHttpMessageHandler([]))
            )
            .RunAsync(["validate", "--dir", registryPath]);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Build_Checked_In_Angular_Package_With_Content()
    {
        var repositoryRoot = FindRepositoryRoot();
        var registryPath = Path.Combine(repositoryRoot, "registry");
        var outputDirectory = Path.Combine(_root, "angular-dist");
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        await using var provider = services.BuildServiceProvider();
        var application = new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            new RegistryPublisher(new HttpClient()),
            provider.GetRequiredService<IHttpClientFactory>()
        );

        var exitCode = await application.RunAsync(
            ["build", "angular", "--dir", registryPath, "--output", outputDirectory]
        );

        exitCode.ShouldBe(0);
        var packagePath = Path.Combine(outputDirectory, "angular@latest.db");
        File.Exists(packagePath).ShouldBeTrue();

        await using var connection = new SqliteConnection($"Data Source={packagePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_id, document_count, chunk_count FROM manifest";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetString(0).ShouldBe("angular");
        reader.GetInt32(1).ShouldBeGreaterThan(0);
        reader.GetInt32(2).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_Build_Versioned_Zip_Definition_From_Manager_Directory()
    {
        var registryDirectory = Path.Combine(_root, "registry", "pip");
        var outputDirectory = Path.Combine(_root, "dist");
        Directory.CreateDirectory(registryDirectory);
        File.WriteAllText(
            Path.Combine(registryDirectory, "python.yaml"),
            """
            name: python
            description: Python documentation
            versions:
              - versions: ["3.14"]
                source:
                  type: zip
                  url: "https://docs.example.test/python-{version}.zip"
                  docs_path: "python-{version}-docs"
                  exclude_paths:
                    - "skip/**"
            """
        );

        var zip = CreateZip(
            ("python-3.14-docs/guide.md", "# Python Guide\nKeep this document."),
            ("python-3.14-docs/skip/internal.md", "# Must be excluded")
        );
        var handler = new RecordingHttpMessageHandler(zip);
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory(handler));
        await using var provider = services.BuildServiceProvider();
        var application = new RegistryApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            NullLoggerFactory.Instance,
            new RegistryPublisher(new HttpClient()),
            provider.GetRequiredService<IHttpClientFactory>()
        );

        var exitCode = await application.RunAsync(
            [
                "build",
                "python",
                "3.14",
                "--dir",
                Path.Combine(_root, "registry"),
                "--output",
                outputDirectory,
            ]
        );

        exitCode.ShouldBe(0);
        handler.RequestedUri.ShouldBe("https://docs.example.test/python-3.14.zip");

        var packagePath = Path.Combine(outputDirectory, "python@3.14.db");
        File.Exists(packagePath).ShouldBeTrue();
        await using var connection = new SqliteConnection($"Data Source={packagePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_id, version, document_count FROM manifest";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetString(0).ShouldBe("python");
        reader.GetString(1).ShouldBe("3.14");
        reader.GetInt32(2).ShouldBe(1);
        await reader.CloseAsync();

        command.CommandText = "SELECT COUNT(*) FROM documents WHERE path LIKE 'skip/%'";
        var excludedDocumentCount = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
        excludedDocumentCount.ShouldBe(0L);
        await connection.CloseAsync();

        var catalogDirectory = Path.Combine(_root, "catalog");
        var indexPath = await new RegistryCatalogService().CreateAsync(
            Path.Combine(_root, "registry"), outputDirectory, catalogDirectory,
            "https://github.com/example/groundkit/releases/download/registry-test/",
            TestContext.Current.CancellationToken);
        var catalog = JsonSerializer.Deserialize<RegistryCatalog>(await File.ReadAllTextAsync(indexPath,
            TestContext.Current.CancellationToken))!;
        catalog.SchemaVersion.ShouldBe(1);
        var entry = catalog.Packages.Single();
        entry.Registry.ShouldBe("pip");
        entry.Name.ShouldBe("python");
        entry.Version.ShouldBe("3.14");
        var bytes = await File.ReadAllBytesAsync(packagePath, TestContext.Current.CancellationToken);
        entry.Size.ShouldBe(bytes.Length);
        entry.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        entry.DownloadUrl.ShouldBe("https://github.com/example/groundkit/releases/download/registry-test/" + entry.Sha256 + ".db");
        File.ReadAllBytes(Path.Combine(catalogDirectory, "assets", entry.Sha256 + ".db")).ShouldBe(bytes);

        using var catalogHttp = new HttpClient(new CatalogHttpMessageHandler(
            await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken), bytes, entry.DownloadUrl))
        { BaseAddress = new Uri("https://catalog.example.test/registry/index.json/") };
        var consumerStore = new SqlitePackageStore(new PackageStoreOptions(Path.Combine(_root, "consumer")),
            NullLogger<SqlitePackageStore>.Instance);
        var installer = new PackageDownloadService(new ContextRegistryClient(catalogHttp), consumerStore,
            provider.GetRequiredService<IDocumentPackageBuilder>(), new TestHttpClientFactory(handler),
            new GroundKitOptions(), NullLogger<PackageDownloadService>.Instance);
        var cli = new GroundKit.Cli.CliApplication(
            provider.GetRequiredService<IDocumentPackageBuilder>(),
            consumerStore,
            new ContextRegistryClient(catalogHttp),
            installer
        );
        var installExitCode = await cli.RunAsync(["install", "pip/python", "3.14"]);
        installExitCode.ShouldBe(0);
        var installed = await consumerStore.GetPackageAsync("python", TestContext.Current.CancellationToken);
        installed.ShouldNotBeNull();
        File.Exists(installed.PackagePath).ShouldBeTrue();
        installed.Version.ShouldBe("3.14");
        installed.DocumentCount.ShouldBe(1);

        var query = await consumerStore.QueryAsync(
            new DocsQueryRequest("python", "Keep this document"),
            TestContext.Current.CancellationToken
        );
        query.PackageId.ShouldBe("python");
        query.Version.ShouldBe("3.14");
        query.Hits.Any(hit => hit.Content.Contains("Keep this document", StringComparison.Ordinal))
            .ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] CreateZip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(file.Path).Open(), Encoding.UTF8);
                writer.Write(file.Content);
            }
        }
        return stream.ToArray();
    }

    private static IDocumentPackageBuilder CreatePackageBuilder()
    {
        var services = new ServiceCollection();
        services.AddGroundKitServices();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDocumentPackageBuilder>();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GroundKit.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CatalogHttpMessageHandler(byte[] catalog, byte[] package, string assetUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsoluteUri == assetUrl
                    ? new ByteArrayContent(package)
                    : request.RequestUri.AbsolutePath == "/registry/index.json"
                        ? new ByteArrayContent(catalog)
                        : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            });
    }

    private sealed class RecordingHttpMessageHandler(byte[] responseBody) : HttpMessageHandler
    {
        public string? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestedUri = request.RequestUri?.ToString();
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(responseBody),
                }
            );
        }
    }
}
