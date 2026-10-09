using System.Text.Json;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Tests.Integration;

public sealed class GroundKitMcpToolsIntegrationTests
{
    [Fact]
    public async Task Should_Return_Structured_Query_Hit_Payload()
    {
        var packageStore = new RecordingPackageStore();
        var responseAgent = new GroundKitToolResponseAgent(
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider()
        );

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "groundkit",
            "refresh package",
            packageStore,
            responseAgent,
            CancellationToken.None,
            maxTokens: 500,
            maxHits: 5,
            relativeScoreCutoff: 0.5
        );

        result.IsError.ShouldNotBe(true);
        result.Content.ShouldNotBeEmpty();
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packageId").GetString().ShouldBe("groundkit");
        structured.GetProperty("hits").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Should_Return_Source_Match_Payload()
    {
        var packageStore = new RecordingPackageStore();
        var responseAgent = new GroundKitToolResponseAgent(
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider()
        );

        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "GroundKit",
            packageStore,
            responseAgent,
            CancellationToken.None,
            maxResults: 5
        );

        result.IsError.ShouldNotBe(true);
        result.Content.ShouldNotBeEmpty();
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packages").GetArrayLength().ShouldBe(1);
    }

    private sealed class RecordingPackageStore : IPackageStore
    {
        private static readonly IReadOnlyList<PackageSummary> Packages =
        [
            new PackageSummary(
                "groundkit",
                "GroundKit",
                "v1.2.3",
                3,
                12,
                DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                "/packages/groundkit@v1.2.3.db"
            ),
        ];

        public Task<string> SaveAsync(
            BuildResult buildResult,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(string.Empty);

        public Task<string> ImportAsync(
            string packageFilePath,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(string.Empty);

        public Task<string> ExportAsync(
            string packageId,
            string destinationPath,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<PackageSummary>> ListAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Packages);

        public Task<PackageSummary?> GetPackageAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                Packages.FirstOrDefault(package =>
                    package.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                )
            );

        public Task<int> RemoveAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(0);

        public Task<int> RemoveAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(0);

        public Task<DocumentationSource?> GetSourceAsync(
            string packageId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<DocumentationSource?>(null);

        public Task<DocsQueryResponse> QueryAsync(
            DocsQueryRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var response = new DocsQueryResponse(
                request.PackageId,
                "v1.2.3",
                [
                    new DocsQueryHit(
                        "Getting Started",
                        "Refresh Command",
                        "Use refresh to rebuild the installed package from stored source metadata.",
                        24,
                        false,
                        0.93
                    ),
                ],
                24
            );

            return Task.FromResult(response);
        }
    }
}
