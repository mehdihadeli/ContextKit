using System.Text.Json;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;
using GroundKit.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace GroundKit.Tests.Unit;

public sealed class McpToolTests
{
    [Fact]
    public async Task Should_Resolve_Source_For_Valid_Query()
    {
        var store = CreateStore();
        var agent = CreateAgent();

        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "groundkit",
            store,
            agent,
            CancellationToken.None,
            maxResults: 5
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packages").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Should_Reject_Empty_Source_Query()
    {
        var store = CreateStore();
        var agent = CreateAgent();

        var result = await GroundKitMcpTools.ResolveSourceAsync(
            "",
            store,
            agent,
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Should_Query_Docs_For_Valid_Request()
    {
        var store = CreateStore();
        var agent = CreateAgent();

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "groundkit",
            "refresh",
            store,
            agent,
            CancellationToken.None,
            maxTokens: 500,
            maxHits: 5,
            relativeScoreCutoff: 0.5
        );

        result.IsError.ShouldNotBe(true);
        var structured = result.StructuredContent.ShouldBeOfType<JsonElement>();
        structured.GetProperty("packageId").GetString().ShouldBe("groundkit");
        structured.GetProperty("hits").GetArrayLength().ShouldBe(1);
    }

    [Theory]
    [InlineData("", "topic", "'packageId'")]
    [InlineData("package", "", "'topic'")]
    public async Task Should_Reject_Missing_Required_Query_Argument(
        string packageId,
        string topic,
        string expectedInMessage
    )
    {
        var store = CreateStore();
        var agent = CreateAgent();

        var result = await GroundKitMcpTools.QueryDocsAsync(
            packageId,
            topic,
            store,
            agent,
            CancellationToken.None
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain(expectedInMessage);
    }

    [Theory]
    [InlineData(0, 5, 0.5, "maxTokens")]
    [InlineData(100, 0, 0.5, "maxHits")]
    [InlineData(100, 5, -0.1, "relativeScoreCutoff")]
    [InlineData(100, 5, 1.1, "relativeScoreCutoff")]
    public async Task Should_Reject_Invalid_Query_Options(
        int maxTokens,
        int maxHits,
        double relativeScoreCutoff,
        string expectedArgument
    )
    {
        var store = CreateStore();
        var agent = CreateAgent();

        var result = await GroundKitMcpTools.QueryDocsAsync(
            "groundkit",
            "topic",
            store,
            agent,
            CancellationToken.None,
            maxTokens: maxTokens,
            maxHits: maxHits,
            relativeScoreCutoff: relativeScoreCutoff
        );

        result.IsError.ShouldBe(true);
        GetText(result.Content).ShouldContain(expectedArgument);
    }

    private static IPackageStore CreateStore()
    {
        var store = Substitute.For<IPackageStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns(Packages);
        store
            .GetPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                Packages.FirstOrDefault(package =>
                    package.PackageId.Equals(
                        callInfo.Arg<string>(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            );
        store
            .QueryAsync(Arg.Any<DocsQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<DocsQueryRequest>();
                return new DocsQueryResponse(
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
            });
        return store;
    }

    private static GroundKitToolResponseAgent CreateAgent() =>
        new(NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

    private static string GetText(IList<ContentBlock> content)
    {
        var textBlock = content.OfType<TextContentBlock>().FirstOrDefault();
        return textBlock?.Text ?? string.Empty;
    }

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
}
