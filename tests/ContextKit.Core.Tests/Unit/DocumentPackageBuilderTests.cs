using System.Net;
using System.Net.Http.Headers;
using GroundKit.Core.Contracts;
using GroundKit.Ingestion.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroundKit.Core.Tests.Unit;

public sealed class DocumentPackageBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentPackageBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
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
    public async Task Should_Build_Manifest_And_Chunks_For_Local_Directory()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "intro.md"),
            "# Introduction\n\nThis is the intro.\n\n## Details\n\nMore details here.",
            TestContext.Current.CancellationToken
        );

        var builder = CreateBuilder();
        var result = await builder.BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Source.Kind.ShouldBe(SourceKind.LocalDirectory);
        result.Manifest.DisplayName.ShouldBe("docs");
        result.Manifest.DocumentCount.ShouldBeGreaterThanOrEqualTo(1);
        result.Manifest.ChunkCount.ShouldBeGreaterThanOrEqualTo(2);
        result.Chunks.ShouldAllBe(chunk => !string.IsNullOrWhiteSpace(chunk.ChunkId));
    }

    [Fact]
    public async Task Should_Use_Local_Directory_Root_When_Docs_Folder_Is_Missing()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "readme.md"),
            "# Readme\n\nContent.",
            TestContext.Current.CancellationToken
        );

        var builder = CreateBuilder();
        var result = await builder.BuildAsync(_tempRoot, cancellationToken: TestContext.Current.CancellationToken);

        result.Manifest.DocumentCount.ShouldBeGreaterThanOrEqualTo(1);
        result.Manifest.ChunkCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Should_Warn_When_Local_Directory_Has_No_Documentation()
    {
        var emptyPath = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(emptyPath);

        var builder = CreateBuilder();

        var result = await builder.BuildAsync(emptyPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Manifest.DocumentCount.ShouldBe(0);
        result.Manifest.ChunkCount.ShouldBe(0);
        result.Warnings.ShouldContain(warning =>
            warning.Code == BuildWarningCode.LowSectionCount
            && warning.Message.Contains("No documentation content was found.")
            && warning.Message.Contains("https://www.perplexity.ai/search/new?q=")
        );
    }

    [Fact]
    public async Task Should_Skip_Excluded_Paths_In_Local_Directory()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsPath, "node_modules"));
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "node_modules", "hidden.md"),
            "# Hidden\n\nShould be skipped.",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "visible.md"),
            "# Visible\n\nShould be included.",
            TestContext.Current.CancellationToken
        );

        var builder = CreateBuilder();
        var result = await builder.BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Documents.ShouldNotContain(document => document.Path.Contains("node_modules"));
        result.Documents.ShouldContain(document => document.Path.Contains("visible.md"));
    }

    [Fact]
    public async Task Should_Deduplicate_Identical_Sections()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "dup.md"),
            "# Title\n\nSame paragraph.\n\n# Title\n\nSame paragraph.",
            TestContext.Current.CancellationToken
        );

        var builder = CreateBuilder();
        var result = await builder.BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Warnings.ShouldContain(warning =>
            warning.Code == BuildWarningCode.DuplicateChunkSkipped
        );
    }

    [Fact]
    public async Task Should_Fetch_Linked_Documents_From_Llms_Index()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://docs.example.com/llms-full.txt"] = NotFound(),
                ["https://docs.example.com/llms.txt"] = TextResponse(
                    "# Docs\n\n[Guide](/guide.md)"
                ),
                ["https://docs.example.com/guide.md"] = TextResponse(
                    "# Guide\n\nInstall and configure."
                ),
            }
        );

        var result = await CreateBuilder(factory).BuildAsync(
            "https://docs.example.com",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Source.Kind.ShouldBe(SourceKind.LlmsText);
        result.Manifest.DocumentCount.ShouldBe(2);
        result.Documents.ShouldContain(document => document.Path == "guide.md");
        result.Documents.ShouldContain(document => document.Title == "Guide");
    }

    [Fact]
    public async Task Should_Prefer_Llms_Full_From_Website_Root()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://docs.example.com/llms-full.txt"] = TextResponse(
                    "# Complete Docs\n\nAll documentation is included here."
                ),
                ["https://docs.example.com/llms.txt"] = TextResponse("# Index"),
            }
        );

        var result = await CreateBuilder(factory).BuildAsync(
            "https://docs.example.com",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Source.Kind.ShouldBe(SourceKind.LlmsText);
        result.Documents.ShouldHaveSingleItem();
        result.Documents[0].Path.ShouldBe("llms-full.txt");
        result.Documents[0].Title.ShouldBe("Complete Docs");
        result.Documents[0].Content.ShouldContain("All documentation is included here.");
    }

    [Fact]
    public async Task Should_Keep_Linked_Documents_When_One_Link_Fails()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://docs.example.com/llms-full.txt"] = NotFound(),
                ["https://docs.example.com/llms.txt"] = TextResponse(
                    "# Docs\n\n[Guide](/guide.md)\n\n[Missing](/missing.md)"
                ),
                ["https://docs.example.com/guide.md"] = TextResponse(
                    "# Guide\n\nInstall and configure."
                ),
                ["https://docs.example.com/missing.md"] = NotFound(),
            }
        );

        var result = await CreateBuilder(factory).BuildAsync(
            "https://docs.example.com",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Documents.Count.ShouldBe(2);
        result.Documents.ShouldContain(document => document.Path == "guide.md");
        result.Documents.ShouldNotContain(document => document.Path == "missing.md");
        result.Warnings.ShouldContain(warning =>
            warning.Code == BuildWarningCode.SourceFetchFallback
            && warning.SourcePath == "https://docs.example.com/missing.md"
        );
    }

    [Fact]
    public async Task Should_Fall_Back_To_Reading_Website_Page_When_Llms_Is_Missing()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://example.com/llms-full.txt"] = NotFound(),
                ["https://example.com/llms.txt"] = NotFound(),
                ["https://example.com/"] = HtmlResponse(
                    "<html><head><title>Article</title></head><body><nav>Subscribe</nav><main><article><h1>Article</h1><p>Useful content.</p></article></main><footer>Comments</footer></body></html>"
                ),
            }
        );

        var result = await CreateBuilder(factory).BuildAsync(
            "https://example.com",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Documents.ShouldHaveSingleItem();
        result.Documents[0].Content.ShouldContain("Useful content.");
        result.Documents[0].Content.ShouldNotContain("Subscribe");
        result.Documents[0].Content.ShouldNotContain("Comments");
    }

    [Fact]
    public async Task Should_Load_GitHub_Blob_Markdown_From_Raw_Content_Url()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://raw.githubusercontent.com/agentgateway/agentgateway/main/README.md"] =
                    TextResponse("# Agentgateway\n\nRaw Markdown content."),
            }
        );

        var result = await CreateBuilder(factory)
            .BuildAsync(
                "https://github.com/agentgateway/agentgateway/blob/main/README.md",
                cancellationToken: TestContext.Current.CancellationToken
            );

        result.Source.Kind.ShouldBe(SourceKind.RawPage);
        result.Source.Location.ShouldBe(
            "https://github.com/agentgateway/agentgateway/blob/main/README.md"
        );
        result.Documents.ShouldHaveSingleItem();
        result.Documents[0].Content.ShouldContain("Raw Markdown content.");
    }

    [Fact]
    public async Task Should_Extract_Title_Headings_And_Code_From_Raw_Html_Page()
    {
        var factory = new TestHttpClientFactory(
            new Dictionary<string, HttpResponseMessage>
            {
                ["https://example.com/article"] = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><title>Article</title></head><body><nav>Subscribe</nav><article><h1>Intro</h1><p>Hello</p><pre><code>dotnet run</code></pre></article><footer>Comments</footer></body></html>",
                        MediaTypeHeaderValue.Parse("text/html")
                    ),
                },
            }
        );

        var result = await CreateBuilder(factory).BuildAsync(
            "https://example.com/article",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Source.Kind.ShouldBe(SourceKind.RawPage);
        result.Documents.Single().Title.ShouldBe("Article");
        result.Documents.Single().Content.ShouldContain("# Intro");
        result.Documents.Single().Content.ShouldContain("```text");
        result.Documents.Single().Content.ShouldContain("dotnet run");
        result.Documents.Single().Content.ShouldNotContain("Subscribe");
        result.Documents.Single().Content.ShouldNotContain("Comments");
    }

    [Fact]
    public async Task Should_Title_A_Nested_Section_With_Its_Heading_Path()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            """
            # Guide

            ## Middleware

            Middleware runs before a handler.

            ### Auth

            Auth middleware validates a token.
            """,
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        // The child keeps the parent in its title, so a reader can tell "Auth" from a sibling "Auth".
        result.Chunks.Select(chunk => chunk.SectionTitle).ShouldContain("Middleware > Auth");
        result
            .Chunks.Single(chunk => chunk.SectionTitle == "Middleware > Auth")
            .Content.ShouldContain("Auth middleware validates a token.");
    }

    [Fact]
    public async Task Should_Not_Repeat_The_Document_Title_In_A_Section_Path()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            "# Guide\n\n## Setup\n\nInstall it.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        // The H1 is already the document title, which every result reports separately.
        result.Chunks.ShouldHaveSingleItem().SectionTitle.ShouldBe("Setup");
    }

    [Fact]
    public async Task Should_Not_Emit_A_Section_That_Contains_Only_Its_Title()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            "# Guide\n\n## Setup\n\nInstall it.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        // A document that opens with an H1 immediately followed by an H2 used to produce a section whose
        // entire body was the title line.
        result.Chunks.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Should_Ignore_A_Heading_Inside_A_Fenced_Code_Block()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "install.md"),
            """
            ## Usage

            Run the tool.

            ```bash
            # install first
            npm i groundkit
            ```

            Then continue.
            """,
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        // The shell comment is part of the sample, not a new section.
        var chunk = result.Chunks.ShouldHaveSingleItem();
        chunk.SectionTitle.ShouldBe("Usage");
        chunk.Content.ShouldContain("# install first");
        chunk.Content.ShouldContain("Then continue.");
    }

    [Fact]
    public async Task Should_Ignore_A_Heading_Inside_A_Tilde_Fenced_Code_Block()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "install.md"),
            "## Usage\n\n~~~\n# not a heading\n~~~\n\nDone.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Chunks.ShouldHaveSingleItem().SectionTitle.ShouldBe("Usage");
    }

    [Fact]
    public async Task Should_Still_Split_On_A_Heading_Outside_A_Code_Block()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            "## Usage\n\nSome text.\n\n# Real Heading\n\nMore text.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        // Fence awareness must not disable headings altogether.
        result.Chunks.Select(chunk => chunk.SectionTitle).ShouldBe(["Usage", "Real Heading"]);
    }

    [Fact]
    public async Task Should_Flag_A_Section_That_Contains_A_Code_Block()
    {
        var docsPath = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "samples.md"),
            "## Code\n\n```csharp\nvar x = 1;\n```\n\n## Prose\n\nNo code here.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder()
            .BuildAsync(docsPath, cancellationToken: TestContext.Current.CancellationToken);

        result.Chunks.Single(chunk => chunk.SectionTitle == "Code").HasCode.ShouldBeTrue();
        result.Chunks.Single(chunk => chunk.SectionTitle == "Prose").HasCode.ShouldBeFalse();
    }

    [Theory]
    [InlineData("````bash", "```", "````")]
    [InlineData("~~~bash", "```", "~~~")]
    [InlineData("```bash", "```not-a-close", "```")]
    [InlineData("   ```bash", "``", "   ```")]
    public async Task Should_Keep_Code_Intact_Until_A_Valid_Closing_Fence(
        string opening,
        string invalidClosing,
        string closing
    )
    {
        var docsPath = Path.Combine(_tempRoot, "fences");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            $"## Usage\n\n{opening}\n{invalidClosing}\n# inside sample\n{closing}\n\n## Next\n\nContinue.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder().BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Chunks.Select(chunk => chunk.SectionTitle).ShouldBe(["Usage", "Next"]);
        result.Chunks[0].Content.ShouldContain("# inside sample");
        result.Chunks[0].HasCode.ShouldBeTrue();
        result.Chunks[1].HasCode.ShouldBeFalse();
        result.Chunks[0].NextChunkId.ShouldBe(result.Chunks[1].ChunkId);
        result.Chunks[1].PreviousChunkId.ShouldBe(result.Chunks[0].ChunkId);
    }

    [Fact]
    public async Task Should_Reset_Heading_Context_For_Sibling_Sections()
    {
        var docsPath = Path.Combine(_tempRoot, "siblings");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, "guide.md"),
            "# Guide\n## Middleware\n### Auth\nValidate tokens.\n## Routing\n### Auth\nProtect routes.",
            TestContext.Current.CancellationToken
        );

        var result = await CreateBuilder().BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Chunks.Select(chunk => chunk.SectionTitle)
            .ShouldBe(["Middleware > Auth", "Routing > Auth"]);
        result.Chunks[0].Content.ShouldContain("Validate tokens.");
        result.Chunks[1].Content.ShouldContain("Protect routes.");
    }

    [Theory]
    [InlineData("README.md", false, true)]
    [InlineData("readme.mdx", false, true)]
    [InlineData("README.en.md", false, true)]
    [InlineData("guide.md", false, false)]
    [InlineData("readme-guide.md", false, false)]
    [InlineData("README.md", true, false)]
    public async Task Should_Warn_About_Readme_Only_Documentation_Without_Penalizing_Guides(
        string filename,
        bool includeGuide,
        bool expectWarning
    )
    {
        var docsPath = Path.Combine(_tempRoot, "quality-docs");
        Directory.CreateDirectory(docsPath);
        await File.WriteAllTextAsync(
            Path.Combine(docsPath, filename),
            "# Library\n## Install\nInstall it.\n## Usage\nUse it.\n## API\nCall it.",
            TestContext.Current.CancellationToken
        );
        if (includeGuide)
        {
            await File.WriteAllTextAsync(
                Path.Combine(docsPath, "guide.md"),
                "# Guide\n\nDetailed configuration.",
                TestContext.Current.CancellationToken
            );
        }

        var result = await CreateBuilder().BuildAsync(
            docsPath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Warnings.Any(warning => warning.Code == BuildWarningCode.ReadmeOnly)
            .ShouldBe(expectWarning);
        result.Warnings.ShouldNotContain(warning => warning.Code == BuildWarningCode.LowSectionCount);
        result.Manifest.WarningCount.ShouldBe(result.Warnings.Count);
        result.Chunks.Count.ShouldBe(includeGuide ? 4 : 3);
        if (expectWarning)
        {
            result.Warnings.Single(warning => warning.Code == BuildWarningCode.ReadmeOnly)
                .Message.ShouldContain("Dedicated guides or API reference may be missing.");
        }
    }

    private DocumentPackageBuilder CreateBuilder(IHttpClientFactory? httpClientFactory = null)
    {
        if (httpClientFactory is null)
        {
            httpClientFactory = Substitute.For<IHttpClientFactory>();
            httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        }

        return new DocumentPackageBuilder(
            new SourceDetector(),
            httpClientFactory,
            NullLogger<DocumentPackageBuilder>.Instance
        );
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, HttpResponseMessage> _responses;

        public TestHttpClientFactory(IReadOnlyDictionary<string, HttpResponseMessage> responses)
        {
            _responses = responses;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new TestMessageHandler(_responses));
        }
    }

    private sealed class TestMessageHandler(
        IReadOnlyDictionary<string, HttpResponseMessage> responses
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (responses.TryGetValue(request.RequestUri!.ToString(), out var response))
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var clone = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(content),
                };
                if (response.Content.Headers.ContentType is not null)
                {
                    clone.Content.Headers.ContentType = response.Content.Headers.ContentType;
                }

                return clone;
            }

            return NotFound();
        }
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage TextResponse(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static HttpResponseMessage HtmlResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, MediaTypeHeaderValue.Parse("text/html")),
        };
}
