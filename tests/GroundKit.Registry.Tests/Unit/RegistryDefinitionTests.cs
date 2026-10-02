using GroundKit.Registry;
using GroundKit.Core.Contracts;

namespace GroundKit.Registry.Tests.Unit;

public sealed class RegistryDefinitionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "groundkit-registry-tests",
        Guid.NewGuid().ToString("n")
    );

    public RegistryDefinitionTests()
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
    public void Should_Load_Definitions_From_Package_Root()
    {
        var path = WriteDefinition(
            "react.yaml",
            """
            name: react
            description: UI library
            source:
              type: git
              url: https://github.com/facebook/react.git
              docs_path: docs
            """
        );

        var definition = RegistryDefinitionLoader.LoadFile(path);

        definition.Name.ShouldBe("react");
        definition.Registry.ShouldBe(RegistryDefinitionLoader.DefaultRegistry);
        definition.DocsPath.ShouldBe("docs");
        definition.ResolveVersions().ShouldBe([("latest", (string?)null)]);
    }

    [Fact]
    public void Should_Resolve_Explicit_Version_Tags()
    {
        var path = WriteDefinition(
            "react.yaml",
            "name: react\nversions:\n  - version: \"19.0.0\"\n    tag: \"v19.0.0\"\n    source:\n      type: git\n      url: https://github.com/facebook/react.git\n  - version: \"19.1.0\"\n    source:\n      type: git\n      url: https://github.com/facebook/react.git\n"
        );

        var definition = RegistryDefinitionLoader.LoadFile(path);
        var versions = definition.ResolveVersions();

        versions.ShouldBe([("19.0.0", (string?)"v19.0.0"), ("19.1.0", (string?)"v19.1.0")]);
    }

    [Fact]
    public void Should_Load_Unversioned_Git_Ref_And_Exclusions()
    {
        var path = WriteDefinition(
            "godot.yaml",
            """
            name: godot
            source:
              type: git
              url: https://github.com/godotengine/godot-docs
              ref: stable
              docs_path: docs
              exclude_paths:
                - "tutorials/scripting/c_sharp/**"
            """
        );

        var definition = RegistryDefinitionLoader.LoadFile(path, "godot");

        definition.ResolveVersions().ShouldBe([("latest", (string?)"stable")]);
        definition.ExcludePaths.ShouldBe(["tutorials/scripting/c_sharp/**"]);
        definition.DocsPath.ShouldBe("docs");
    }

    [Fact]
    public void Should_Load_Versioned_Zip_With_Grouped_Versions()
    {
        var path = WriteDefinition(
            "python.yaml",
            """
            name: python
            versions:
              - versions: ["3.14", "3.13"]
                source:
                  type: zip
                  url: https://docs.example.test/python-{version}.zip
                  docs_path: python-{version}-docs
                  exclude_paths:
                    - changelog.html
            """
        );

        var definition = RegistryDefinitionLoader.LoadFile(path, "pip");

        definition.Kind.ShouldBe(SourceKind.ZipArchive);
        definition.Versions.Select(version => version.Version).ShouldBe(["3.14", "3.13"]);
        definition.Versions.All(version => version.Source!.Contains("{version}")).ShouldBeTrue();
        definition.Versions
            .Select(version => version.ExcludePaths)
            .All(exclusions => exclusions is not null && exclusions.SequenceEqual(["changelog.html"]))
            .ShouldBeTrue();
    }

    [Fact]
    public void Should_Load_Versioned_Git_Minimum_Version_And_Tag_Pattern()
    {
        var path = WriteDefinition(
            "next.yaml",
            """
            name: next
            versions:
              - min_version: "15.0.0"
                tag_pattern: "v{version}"
                source:
                  type: git
                  url: https://github.com/vercel/next.js
                  docs_path: docs
            """
        );

        var definition = RegistryDefinitionLoader.LoadFile(path, "npm");

        definition.Kind.ShouldBe(SourceKind.GitRepository);
        definition.ResolveVersions().ShouldBe([("15.0.0", (string?)"v15.0.0")]);
    }

    [Fact]
    public void Should_Load_Versioned_Html_Index()
    {
        var path = WriteDefinition(
            "systemd.yaml",
            """
            name: systemd
            versions:
              - versions: ["258"]
                source:
                  type: html-index
                  url: https://docs.example.test/systemd/{version}/
                  exclude_paths:
                    - index.html
            """
        );

        var definition = RegistryDefinitionLoader.LoadFile(path, "systemd");

        definition.Kind.ShouldBe(SourceKind.HtmlIndex);
        definition.Versions.Single().ExcludePaths.ShouldBe(["index.html"]);
    }

    [Fact]
    public void Should_Reject_Source_And_Versions_At_The_Same_Level()
    {
        var path = WriteDefinition(
            "invalid.yaml",
            """
            name: invalid
            source:
              type: git
              url: https://github.com/example/docs
            versions:
              - version: "1.0.0"
                source:
                  type: git
                  url: https://github.com/example/docs
            """
        );

        Should.Throw<InvalidDataException>(() => RegistryDefinitionLoader.LoadFile(path));
    }

    [Fact]
    public void Should_Resolve_Scoped_And_Nested_Package_Names()
    {
                var scopedPath = Path.Combine(_tempRoot, "npm", "@trpc", "server.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(scopedPath)!);
                File.WriteAllText(
                        scopedPath,
                        """
                        name: "@trpc/server"
                        source:
                            type: git
                            url: https://github.com/trpc/trpc
                        """
                );
                var goPath = Path.Combine(_tempRoot, "go", "github.com", "spf13", "cobra.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(goPath)!);
                File.WriteAllText(
                        goPath,
                        """
                        name: github.com/spf13/cobra
                        source:
                            type: git
                            url: https://github.com/spf13/cobra
                        """
                );

                var definitions = RegistryDefinitionLoader.LoadDirectory(_tempRoot);

            definitions.Select(definition => (definition.Registry, definition.Name)).ShouldBe(
                [
                ("go", "github.com/spf13/cobra"),
                ("npm", "@trpc/server"),
                ]
            );
            }

    [Fact]
    public void Should_Reject_Name_That_Does_Not_Match_Filename()
    {
        var path = WriteDefinition(
            "react.yaml",
            """
            name: vue
            source:
              type: git
              url: https://github.com/vuejs/core.git
            """
        );

        Should
            .Throw<InvalidDataException>(() => RegistryDefinitionLoader.LoadFile(path))
            .Message.ShouldContain("does not match filename");
    }

    private string WriteDefinition(string fileName, string contents)
    {
        var path = Path.Combine(_tempRoot, fileName);
        var lines = contents.Replace("\r\n", "\n").Split('\n');
        var indent = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        File.WriteAllText(path, string.Join(Environment.NewLine, lines.Select(line =>
            line.Length >= indent ? line[indent..] : line)));
        return path;
    }
}
