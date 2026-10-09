using GroundKit.Cli;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;

namespace GroundKit.Tests.Unit;

public sealed class AddFromWebsiteUnitTests
{
    [Fact]
    public async Task Should_Add_Website_Root_Without_Docs_Path()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
        Configure(builder, store, buildResult);

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "https://agentgateway.dev"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "https://agentgateway.dev",
                null,
                Arg.Any<CancellationToken>(),
                null,
                null,
                null
            );
    }

    [Fact]
    public async Task Should_Add_Direct_Llms_Text_Url()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
        Configure(builder, store, buildResult);

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "https://agentgateway.dev/llms.txt"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "https://agentgateway.dev/llms.txt",
                null,
                Arg.Any<CancellationToken>(),
                null,
                null,
                null
            );
    }

    [Fact]
    public async Task Should_Apply_Custom_Name_To_Website_Package()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
        Configure(builder, store, buildResult);

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "https://agentgateway.dev", "--name", "agent-gateway"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "https://agentgateway.dev",
                null,
                Arg.Any<CancellationToken>(),
                null,
                null,
                "agent-gateway"
            );
    }

    private static void Configure(
        IDocumentPackageBuilder builder,
        IPackageStore store,
        BuildResult buildResult
    )
    {
        builder
            .BuildAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>()
            )
            .Returns(buildResult);
        store.SaveAsync(Arg.Any<BuildResult>(), Arg.Any<CancellationToken>()).Returns("package.db");
    }

    private static CliApplication CreateApplication(
        IDocumentPackageBuilder builder,
        IPackageStore store
    )
    {
        return new CliApplication(builder, store);
    }

    private static BuildResult CreateBuildResult()
    {
        var source = new DocumentationSource(
            SourceKind.LlmsText,
            "agentgateway-dev",
            "agentgateway.dev",
            "https://agentgateway.dev/llms.txt"
        );
        return new BuildResult(
            source,
            new PackageManifest(
                source.CanonicalId,
                source.DisplayName,
                null,
                source.Kind,
                source.Location,
                source.CanonicalId,
                null,
                DateTimeOffset.UtcNow,
                "0.1.0-dev",
                1,
                1,
                0
            ),
            [],
            [new DocumentRecord("doc-1", "Title", "llms.txt", "# Title")],
            [
                new ChunkRecord(
                    "chunk-1",
                    "doc-1",
                    "Title",
                    "Title",
                    "llms.txt",
                    "# Title",
                    2,
                    false,
                    "hash",
                    0
                ),
            ]
        );
    }
}
