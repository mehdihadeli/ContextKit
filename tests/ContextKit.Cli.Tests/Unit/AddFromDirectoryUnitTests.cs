using GroundKit.Cli;
using GroundKit.Core.Abstractions;
using GroundKit.Core.Contracts;

namespace GroundKit.Tests.Unit;

public sealed class AddFromDirectoryUnitTests
{
    [Fact]
    public async Task Should_Pass_Explicit_Path_To_Add()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
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

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "./my-project", "--path", "docs"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync("./my-project", "docs", Arg.Any<CancellationToken>(), null, null, null);
    }

    [Fact]
    public async Task Should_Pass_Custom_Name_And_Package_Version_To_Add()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
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

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "./my-lib", "--name", "my-library", "--pkg-version", "1.0.0"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "./my-lib",
                null,
                Arg.Any<CancellationToken>(),
                "1.0.0",
                null,
                "my-library"
            );
    }

    [Fact]
    public async Task Should_Accept_Docs_Path_Alias()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
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

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "./my-project", "--docs-path", "documentation"]);

        exitCode.ShouldBe(0);
        await builder
            .Received(1)
            .BuildAsync(
                "./my-project",
                "documentation",
                Arg.Any<CancellationToken>(),
                null,
                null,
                null
            );
    }

    [Fact]
    public async Task Should_Save_Copy_Of_Local_Directory_Package()
    {
        var builder = Substitute.For<IDocumentPackageBuilder>();
        var store = Substitute.For<IPackageStore>();
        var buildResult = CreateBuildResult();
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
        store
            .ExportAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("./shared/my-library.db");

        var exitCode = await CreateApplication(builder, store)
            .RunAsync(["add", "./my-lib", "--save", "./shared/my-library.db"]);

        exitCode.ShouldBe(0);
        await store
            .Received(1)
            .ExportAsync(
                buildResult.Manifest.PackageId,
                "./shared/my-library.db",
                Arg.Any<CancellationToken>()
            );
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
            SourceKind.LocalDirectory,
            "my-project",
            "my-project",
            "C:/my-project"
        );
        return new BuildResult(
            source,
            new PackageManifest(
                "my-project",
                "my-project",
                null,
                SourceKind.LocalDirectory,
                "C:/my-project",
                "my-project",
                null,
                DateTimeOffset.UtcNow,
                "0.1.0-dev",
                1,
                1,
                0
            ),
            [],
            [new DocumentRecord("doc-1", "Title", "index.md", "# Title")],
            [
                new ChunkRecord(
                    "chunk-1",
                    "doc-1",
                    "Title",
                    "Title",
                    "index.md",
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
