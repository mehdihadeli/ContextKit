using System.Text;
using GroundKit.Oci;

namespace GroundKit.Registry.Tests.Unit;

/// <summary>
/// Covers the Docker credential fallback that lets <c>docker/login-action</c> stand in for an
/// exported token in workflows.
/// </summary>
/// <remarks>
/// Most cases exercise pure JSON parsing. The two file-system cases point <c>DOCKER_CONFIG</c> at a
/// temporary directory; xUnit runs the tests in a class serially, so owning that process-wide
/// variable here cannot race another class.
/// </remarks>
public sealed class OciDockerCredentialTests
{
    private const string Host = "ghcr.io";

    private static string Encode(string username, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private static string SingleEntry(string key) =>
        $$"""
        { "auths": { "{{key}}": { "auth": "{{Encode("octocat", "s3cret")}}" } } }
        """;

    [Theory]
    [InlineData("ghcr.io")]
    [InlineData("https://ghcr.io")]
    [InlineData("https://ghcr.io/")]
    [InlineData("GHCR.IO")]
    public void Should_Resolve_Host_Regardless_Of_Scheme_Casing_Or_Trailing_Slash(string key)
    {
        var credential = OciCredential.ParseDockerConfig(SingleEntry(key), Host);

        credential.ShouldNotBeNull();
        credential!.Username.ShouldBe("octocat");
        credential.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void Should_Resolve_Docker_Hub_Style_Key_With_Path()
    {
        var credential = OciCredential.ParseDockerConfig(
            SingleEntry("https://index.docker.io/v1/"),
            "index.docker.io"
        );

        credential.ShouldNotBeNull();
        credential!.Username.ShouldBe("octocat");
    }

    [Fact]
    public void Should_Not_Leak_Another_Hosts_Credential()
    {
        var json =
            $$"""
            {
              "auths": {
                "ghcr.io": { "auth": "{{Encode("octocat", "s3cret")}}" },
                "index.docker.io": { "auth": "{{Encode("someone", "else")}}" }
              }
            }
            """;

        OciCredential.ParseDockerConfig(json, "registry.example.test").ShouldBeNull();
    }

    [Fact]
    public void Should_Resolve_The_Only_Entry_When_No_Host_Is_Given() =>
        OciCredential
            .ParseDockerConfig(SingleEntry("ghcr.io"))!
            .Username.ShouldBe("octocat");

    [Fact]
    public void Should_Resolve_Nothing_When_Several_Entries_Are_Ambiguous()
    {
        var json =
            $$"""
            {
              "auths": {
                "ghcr.io": { "auth": "{{Encode("octocat", "s3cret")}}" },
                "index.docker.io": { "auth": "{{Encode("someone", "else")}}" }
              }
            }
            """;

        OciCredential.ParseDockerConfig(json).ShouldBeNull();
    }

    [Fact]
    public void Should_Read_Separate_Username_And_Password_Fields()
    {
        var json = """{ "auths": { "ghcr.io": { "username": "octocat", "password": "s3cret" } } }""";

        var credential = OciCredential.ParseDockerConfig(json, Host);

        credential.ShouldNotBeNull();
        credential!.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void Should_Ignore_Malformed_Base64_Auth()
    {
        var json = """{ "auths": { "ghcr.io": { "auth": "not base64!" } } }""";

        OciCredential.ParseDockerConfig(json, Host).ShouldBeNull();
    }

    [Fact]
    public void Should_Ignore_Auth_Without_Separator()
    {
        var json = $$"""{ "auths": { "ghcr.io": { "auth": "{{Encode("nobody", "")}}" } } }""";

        OciCredential.ParseDockerConfig(json, Host).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{ "auths": {} }""")]
    [InlineData("""{ "auths": { "ghcr.io": {} } }""")]
    [InlineData("""{ "credsStore": "desktop" }""")]
    [InlineData("")]
    public void Should_Resolve_Nothing_When_No_Usable_Entry_Exists(string json) =>
        OciCredential.ParseDockerConfig(json, Host).ShouldBeNull();

    [Fact]
    public void Should_Surface_Malformed_Json() =>
        Should
            .Throw<System.Text.Json.JsonException>(() =>
                OciCredential.ParseDockerConfig("{ not json", Host)
            );

    [Fact]
    public void Should_Read_Credentials_From_The_Configured_Config_File()
    {
        using var directory = new TemporaryDockerConfig(SingleEntry(Host));

        var credential = OciCredential.FromDockerConfig(Host);

        credential.ShouldNotBeNull();
        credential!.Username.ShouldBe("octocat");
    }

    [Fact]
    public void Should_Resolve_Nothing_When_The_Config_File_Is_Absent()
    {
        using var directory = new TemporaryDockerConfig(null);

        OciCredential.FromDockerConfig(Host).ShouldBeNull();
    }

    /// <summary>
    /// The path CI takes: no token is exported, so the login written by <c>docker/login-action</c>
    /// is the only credential source.
    /// </summary>
    [Fact]
    public void Should_Fall_Back_To_The_Config_File_When_No_Token_Is_Exported()
    {
        Assert.SkipUnless(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROUNDKIT_OCI_TOKEN"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN")),
            "A token is exported here, which takes precedence over the Docker credential file."
        );

        using var directory = new TemporaryDockerConfig(SingleEntry(Host));

        var credential = OciCredential.FromEnvironment(Host);

        credential.ShouldNotBeNull();
        credential!.Username.ShouldBe("octocat");
    }

    /// <summary>
    /// Points <c>DOCKER_CONFIG</c> at an isolated directory for the lifetime of the test and
    /// restores the previous value afterwards.
    /// </summary>
    private sealed class TemporaryDockerConfig : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "groundkit-docker-config-" + Guid.NewGuid().ToString("N")
        );

        private readonly string? _previous = Environment.GetEnvironmentVariable("DOCKER_CONFIG");

        public TemporaryDockerConfig(string? content)
        {
            Directory.CreateDirectory(_root);
            if (content is not null)
            {
                File.WriteAllText(Path.Combine(_root, "config.json"), content);
            }

            Environment.SetEnvironmentVariable("DOCKER_CONFIG", _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DOCKER_CONFIG", _previous);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
