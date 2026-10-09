using System.Net;
using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Core.Tests.Unit;

public sealed class PackageDownloadServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        Guid.NewGuid().ToString("n")
    );

    public PackageDownloadServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Should_Import_Existing_Local_Package()
    {
        var packagePath = Path.Combine(_tempRoot, "react@19.1.0.db");
        await File.WriteAllBytesAsync(
            packagePath,
            [1, 2, 3],
            TestContext.Current.CancellationToken
        );
        var importedPath = string.Empty;
        var store = Substitute.For<IPackageStore>();
        store
            .ImportAsync(Arg.Do<string>(path => importedPath = path), Arg.Any<CancellationToken>())
            .Returns("imported.db");
        var service = CreateService(store);

        var installedPath = await service.InstallAsync(
            packagePath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        installedPath.ShouldBe("imported.db");
        importedPath.ShouldBe(packagePath);
    }

    [Fact]
    public async Task Should_Download_And_Import_Remote_Package()
    {
        var importedBytes = Array.Empty<byte>();
        var store = Substitute.For<IPackageStore>();
        store
            .ImportAsync(
                Arg.Do<string>(path => importedBytes = File.ReadAllBytes(path)),
                Arg.Any<CancellationToken>()
            )
            .Returns("imported.db");
        var service = CreateService(store, [9, 8, 7]);

        var installedPath = await service.InstallAsync(
            "https://example.com/packages/react@19.1.0.db",
            cancellationToken: TestContext.Current.CancellationToken
        );

        installedPath.ShouldBe("imported.db");
        importedBytes.ShouldBe([9, 8, 7]);
    }

    [Fact]
    public async Task Should_Download_Remote_Package_Without_Database_Extension()
    {
        var importedBytes = Array.Empty<byte>();
        var store = Substitute.For<IPackageStore>();
        store
            .ImportAsync(
                Arg.Do<string>(path => importedBytes = File.ReadAllBytes(path)),
                Arg.Any<CancellationToken>()
            )
            .Returns("imported.db");
        var service = CreateService(store, [6, 5, 4]);

        var installedPath = await service.InstallAsync(
            "https://localhost/mattpocock-skills@1.2.3",
            cancellationToken: TestContext.Current.CancellationToken
        );

        installedPath.ShouldBe("imported.db");
        importedBytes.ShouldBe([6, 5, 4]);
    }

    [Fact]
    public async Task Should_Search_The_Npm_Registry_For_A_Scoped_Package()
    {
        var store = Substitute.For<IPackageStore>();
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "@angular/core", null, Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("@angular/core", "npm", "21.0.3", null, 512)]);
        var service = CreateService(store, registryClient);

        await service.InstallAsync(
            "@angular/core",
            cancellationToken: TestContext.Current.CancellationToken
        );

        // The leading @ is a scope, not a registry: searching registry "@angular" for "core" would
        // find nothing and silently fall through to a local build of the wrong thing.
        await registryClient
            .Received(1)
            .SearchAsync("npm", "@angular/core", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Honour_Version_Written_In_The_Selector()
    {
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", "19.1.0", Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "19.1.0", null, 1_024)]);
        var service = CreateService(Substitute.For<IPackageStore>(), registryClient);

        await service.InstallAsync(
            "react@19.1.0",
            cancellationToken: TestContext.Current.CancellationToken
        );

        await registryClient
            .Received(1)
            .SearchAsync("npm", "react", "19.1.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Prefer_The_Explicit_Version_Over_The_Selector()
    {
        var registryClient = Substitute.For<IContextRegistryClient>();
        registryClient
            .SearchAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>())
            .Returns([new RegistryPackage("react", "npm", "18.2.0", null, 1_024)]);
        var service = CreateService(Substitute.For<IPackageStore>(), registryClient);

        await service.InstallAsync("react@19.1.0", "18.2.0", TestContext.Current.CancellationToken);

        await registryClient
            .Received(1)
            .SearchAsync("npm", "react", "18.2.0", Arg.Any<CancellationToken>());
        await registryClient
            .DidNotReceive()
            .SearchAsync("npm", "react", "19.1.0", Arg.Any<CancellationToken>());
    }

    private PackageDownloadService CreateService(
        IPackageStore store,
        byte[]? remotePackage = null
    ) =>
        new(
            Substitute.For<IContextRegistryClient>(),
            store,
            null!,
            new TestHttpClientFactory(remotePackage),
            new GroundKitOptions(),
            NullLogger<PackageDownloadService>.Instance
        );

    private PackageDownloadService CreateService(
        IPackageStore store,
        IContextRegistryClient registryClient
    ) =>
        new(
            registryClient,
            store,
            null!,
            new TestHttpClientFactory(null),
            new GroundKitOptions(),
            NullLogger<PackageDownloadService>.Instance
        );
    private sealed class TestHttpClientFactory(byte[]? package) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new PackageHandler(package));
    }

    private sealed class PackageHandler(byte[]? package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                package is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(package),
                    }
            );
    }
}
