using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GroundKit.Oci;

/// <summary>
/// Credentials for an OCI registry. GitHub Container Registry accepts any username together with a
/// personal access token (or the workflow <c>GITHUB_TOKEN</c>) as the password.
/// </summary>
public sealed record OciCredential(string Username, string Password)
{
    /// <summary>
    /// Resolves credentials for <paramref name="registryHost"/>, preferring environment variables
    /// and falling back to the Docker credential file.
    /// </summary>
    /// <remarks>
    /// Resolution order: <c>GROUNDKIT_OCI_USERNAME</c>/<c>GROUNDKIT_OCI_TOKEN</c>, then
    /// <c>GITHUB_ACTOR</c>/<c>GITHUB_TOKEN</c>, then the <c>auths</c> entry in
    /// <c>$DOCKER_CONFIG/config.json</c> (default <c>~/.docker/config.json</c>) whose host matches
    /// <paramref name="registryHost"/>. The Docker file is what <c>docker/login-action</c> writes,
    /// so a workflow logs in with the standard action instead of exporting a token. Public
    /// artifacts pull anonymously, so credentials resolving to <see langword="null"/> is not an
    /// error.
    /// </remarks>
    public static OciCredential? FromEnvironment(string? registryHost = null)
    {
        var token =
            Environment.GetEnvironmentVariable("GROUNDKIT_OCI_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            return FromDockerConfig(registryHost);
        }

        var username =
            Environment.GetEnvironmentVariable("GROUNDKIT_OCI_USERNAME")?.Trim()
            ?? Environment.GetEnvironmentVariable("GITHUB_ACTOR")?.Trim();
        return new OciCredential(
            string.IsNullOrWhiteSpace(username) ? "groundkit" : username,
            token.Trim()
        );
    }

    /// <summary>
    /// Reads credentials for <paramref name="registryHost"/> from the Docker credential file.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the file is absent, unreadable, malformed, or holds no entry for
    /// <paramref name="registryHost"/>. Credential stores and helpers are never invoked, so a
    /// <c>credsStore</c> configuration resolves to <see langword="null"/> instead of being shelled
    /// out to.
    /// </returns>
    public static OciCredential? FromDockerConfig(string? registryHost = null)
    {
        var path = DockerConfigPath();
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return ParseDockerConfig(File.ReadAllText(path), registryHost);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts credentials from Docker configuration JSON, matching entries by registry host so a
    /// file holding several registries never sends one host's token to another. When
    /// <paramref name="registryHost"/> is <see langword="null"/>, only a single-entry file is
    /// accepted, because several entries would be ambiguous.
    /// </summary>
    public static OciCredential? ParseDockerConfig(string json, string? registryHost = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (
            !document.RootElement.TryGetProperty("auths", out var auths)
            || auths.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }

        var candidates = new List<JsonElement>();
        foreach (var entry in auths.EnumerateObject())
        {
            if (registryHost is null || MatchesHost(entry.Name, registryHost))
            {
                candidates.Add(entry.Value);
            }
        }

        // Without a host to disambiguate against, several entries are ambiguous and none is used.
        if (candidates.Count == 0 || (registryHost is null && candidates.Count > 1))
        {
            return null;
        }

        return ReadCredential(candidates[0]);
    }

    private static OciCredential? ReadCredential(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (entry.TryGetProperty("auth", out var auth) && auth.GetString() is { Length: > 0 } encoded)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var separator = decoded.IndexOf(':', StringComparison.Ordinal);

                // Both halves are required: an entry with an empty username or password cannot
                // authenticate, and keeping it would turn a broken config into a confusing 401.
                if (separator > 0 && separator < decoded.Length - 1)
                {
                    return new OciCredential(decoded[..separator], decoded[(separator + 1)..]);
                }
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // Some tools write the pair as separate fields instead of one base64 blob.
        var username = entry.TryGetProperty("username", out var name) ? name.GetString() : null;
        var password = entry.TryGetProperty("password", out var secret) ? secret.GetString() : null;
        return string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
            ? null
            : new OciCredential(username, password);
    }

    private static bool MatchesHost(string key, string registryHost) =>
        string.Equals(
            NormalizeRegistryHost(key),
            NormalizeRegistryHost(registryHost),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    /// Reduces a Docker config key such as <c>https://index.docker.io/v1/</c> to its host, so keys
    /// and hosts compare on the same footing.
    /// </summary>
    private static string NormalizeRegistryHost(string value)
    {
        var normalized = value.Trim();
        var scheme = normalized.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            normalized = normalized[(scheme + 3)..];
        }

        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            normalized = normalized[..slash];
        }

        return normalized.TrimEnd('/');
    }

    private static string DockerConfigPath()
    {
        var directory = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        if (string.IsNullOrWhiteSpace(directory))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return string.Empty;
            }

            directory = Path.Combine(home, ".docker");
        }

        return Path.Combine(directory.Trim(), "config.json");
    }
}

/// <summary>
/// A parsed <c>oci://host/repository:tag</c> or <c>oci://host/repository@sha256:digest</c> locator.
/// </summary>
public sealed record OciReference(string Host, string Repository, string Reference)
{
    private const string SchemePrefix = "oci://";

    public static bool IsOci(string? value) =>
        value is not null
        && value.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts both <c>oci://ghcr.io/owner/name/pkg:latest</c> and the bare
    /// <c>ghcr.io/owner/name/pkg@sha256:...</c> form.
    /// </summary>
    public static OciReference Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (trimmed.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[SchemePrefix.Length..];
        }

        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw new FormatException(
                $"OCI reference '{value}' must be formatted as oci://<host>/<repository>[:tag|@digest]."
            );
        }

        var host = trimmed[..separator];
        var remainder = trimmed[(separator + 1)..];

        var digestIndex = remainder.LastIndexOf('@');
        if (digestIndex > 0)
        {
            var repository = remainder[..digestIndex];
            var digest = remainder[(digestIndex + 1)..];
            if (digest.Length == 0)
            {
                throw new FormatException($"OCI reference '{value}' has an empty digest.");
            }

            return new OciReference(host, repository, digest);
        }

        var tagIndex = remainder.LastIndexOf(':');
        var lastSlash = remainder.LastIndexOf('/');
        if (tagIndex > lastSlash && tagIndex != -1)
        {
            var repository = remainder[..tagIndex];
            var tag = remainder[(tagIndex + 1)..];
            if (tag.Length == 0)
            {
                throw new FormatException($"OCI reference '{value}' has an empty tag.");
            }

            return new OciReference(host, repository, tag);
        }

        return new OciReference(host, remainder, "latest");
    }

    public override string ToString() =>
        $"{SchemePrefix}{Host}/{Repository}{(Reference.StartsWith("sha256:", StringComparison.Ordinal) ? "@" : ":")}{Reference}";
}

/// <summary>An OCI content descriptor.</summary>
public sealed record OciDescriptor(string MediaType, string Digest, long Size);

/// <summary>An OCI image manifest restricted to a single configuration and layer set.</summary>
public sealed record OciManifest(
    string MediaType,
    OciDescriptor Config,
    IReadOnlyList<OciDescriptor> Layers
);

/// <summary>Helpers for mapping registry packages onto OCI repository names.</summary>
public static class OciNames
{
    public const string PackageMediaType = "application/vnd.groundkit.package.v1.db";
    public const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    public const string ConfigMediaType = "application/vnd.oci.image.config.v1+json";

    /// <summary>
    /// OCI repository names must be lowercase and may not contain the <c>/</c> used by registry
    /// names such as <c>github.com/spf13/cobra</c>, so they are flattened into one segment.
    /// </summary>
    public static string RepositoryName(string registry, string name) =>
        Sanitize($"{registry}-{name}");

    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                    ? char.ToLowerInvariant(character)
                    : '-'
            );
        }

        var sanitized = builder.ToString().Trim('-', '.');
        if (sanitized.Length > 128)
        {
            sanitized = sanitized[..128].TrimEnd('-', '.');
        }

        if (sanitized.Length == 0)
        {
            throw new ArgumentException($"'{value}' does not produce a valid OCI repository name.");
        }

        return sanitized;
    }

    public static string Digest(ReadOnlySpan<byte> content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// A minimal client for the OCI Distribution Specification that supports anonymous and
/// bearer-token authenticated blob and manifest transfer.
/// </summary>
public sealed class OciRegistryClient(HttpClient httpClient, OciCredential? credential = null)
{
    private const string BasicRealm = "Basic";

    private static readonly byte[] EmptyConfig = "{}"u8.ToArray();

    private readonly Dictionary<string, AuthenticationHeaderValue> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessChallenge> _challenges = new(StringComparer.Ordinal);

    /// <summary>
    /// Pushes <paramref name="filePath"/> as a single layer and returns the manifest digest.
    /// Blobs already present in the registry are not re-uploaded.
    /// </summary>
    public async Task<string> PushAsync(
        string repository,
        string reference,
        string filePath,
        IReadOnlyDictionary<string, string>? annotations = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var scope = $"repository:{repository}:pull,push";

        var digest = await ComputeFileDigestAsync(filePath, cancellationToken);
        var size = new FileInfo(filePath).Length;
        if (!await BlobExistsAsync(repository, digest, scope, cancellationToken))
        {
            await UploadBlobAsync(repository, digest, filePath, scope, cancellationToken);
        }

        // GHCR rejects a manifest that references a config blob it does not hold, so the empty OCI
        // config is uploaded alongside the layer even though the artifact never reads it.
        var configDigest = OciNames.Digest(EmptyConfig);
        if (!await BlobExistsAsync(repository, configDigest, scope, cancellationToken))
        {
            await UploadBlobAsync(repository, configDigest, EmptyConfig, scope, cancellationToken);
        }

        var manifest = BuildManifest(
            digest,
            size,
            Path.GetFileName(filePath),
            annotations
        );
        return await PutManifestAsync(repository, reference, manifest, scope, cancellationToken);
    }

    public async Task<OciManifest> GetManifestAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken = default
    )
    {
        var scope = $"repository:{repository}:pull";
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"/v2/{repository}/manifests/{reference}"
                );
                request.Headers.Accept.ParseAdd(OciNames.ManifestMediaType);
                request.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
                return request;
            },
            scope,
            cancellationToken
        );

        await EnsureSuccessAsync(response, cancellationToken);
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return ParseManifest(payload);
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> to its single package layer.
    /// </summary>
    public async Task<OciDescriptor> ResolveLayerAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken = default
    )
    {
        var manifest = await GetManifestAsync(repository, reference, cancellationToken);
        return manifest.Layers.Count == 1
            ? manifest.Layers[0]
            : throw new InvalidDataException(
                $"OCI artifact '{repository}:{reference}' must contain exactly one layer, but has {manifest.Layers.Count}."
            );
    }

    public async Task DownloadBlobAsync(
        string repository,
        string digest,
        string destinationPath,
        long? expectedSize = null,
        CancellationToken cancellationToken = default
    )
    {
        var scope = $"repository:{repository}:pull";
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/v2/{repository}/blobs/{digest}"),
            scope,
            cancellationToken
        );
        await EnsureSuccessAsync(response, cancellationToken);

        if (
            expectedSize is { } size
            && response.Content.Headers.ContentLength is { } length
            && length != size
        )
        {
            throw new InvalidDataException("Registry package size does not match the catalog.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task<bool> BlobExistsAsync(
        string repository,
        string digest,
        string scope,
        CancellationToken cancellationToken
    )
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Head, $"/v2/{repository}/blobs/{digest}"),
            scope,
            cancellationToken
        );
        return response.StatusCode == HttpStatusCode.OK;
    }

    private async Task UploadBlobAsync(
        string repository,
        string digest,
        string filePath,
        string scope,
        CancellationToken cancellationToken
    ) =>
        await UploadBlobAsync(
            repository,
            digest,
            () => new StreamContent(File.OpenRead(filePath)),
            scope,
            cancellationToken
        );

    private async Task UploadBlobAsync(
        string repository,
        string digest,
        byte[] content,
        string scope,
        CancellationToken cancellationToken
    ) =>
        await UploadBlobAsync(
            repository,
            digest,
            () => new ByteArrayContent(content),
            scope,
            cancellationToken
        );

    private async Task UploadBlobAsync(
        string repository,
        string digest,
        Func<HttpContent> contentFactory,
        string scope,
        CancellationToken cancellationToken
    )
    {
        using var start = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/v2/{repository}/blobs/uploads/"),
            scope,
            cancellationToken
        );
        await EnsureSuccessAsync(start, cancellationToken);

        var location =
            start.Headers.Location
            ?? throw new InvalidDataException("OCI registry did not return an upload location.");
        var uploadUri = location.IsAbsoluteUri
            ? location
            : new Uri(httpClient.BaseAddress!, location);
        var target = AppendDigest(uploadUri, digest);

        using var upload = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, target);
                request.Content = contentFactory();
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                    "application/octet-stream"
                );
                return request;
            },
            scope,
            cancellationToken
        );
        await EnsureSuccessAsync(upload, cancellationToken);
    }

    /// <summary>
    /// Removes a manifest by tag or digest. Deleting leaves the underlying blobs until the registry
    /// garbage collects them, but it stops a throwaway artifact from staying pullable forever.
    /// </summary>
    public async Task DeleteManifestAsync(
        string repository,
        string reference,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var scope = $"repository:{repository}:pull,push";
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"/v2/{repository}/manifests/{reference}"),
            scope,
            cancellationToken
        );
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<string> PutManifestAsync(
        string repository,
        string reference,
        byte[] manifest,
        string scope,
        CancellationToken cancellationToken
    )
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"/v2/{repository}/manifests/{reference}"
                );
                request.Content = new ByteArrayContent(manifest);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                    OciNames.ManifestMediaType
                );
                return request;
            },
            scope,
            cancellationToken
        );
        await EnsureSuccessAsync(response, cancellationToken);

        return response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.First()
            : OciNames.Digest(manifest);
    }

    private static byte[] BuildManifest(
        string digest,
        long size,
        string title,
        IReadOnlyDictionary<string, string>? annotations
    )
    {
        var layerAnnotations = new JsonObject
        {
            ["org.opencontainers.image.title"] = title,
        };
        if (annotations is not null)
        {
            foreach (var annotation in annotations)
            {
                layerAnnotations[annotation.Key] = annotation.Value;
            }
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciNames.ManifestMediaType,
            ["config"] = new JsonObject
            {
                ["mediaType"] = OciNames.ConfigMediaType,
                ["digest"] = OciNames.Digest(EmptyConfig),
                ["size"] = EmptyConfig.Length,
            },
            ["layers"] = new JsonArray
            {
                new JsonObject
                {
                    ["mediaType"] = OciNames.PackageMediaType,
                    ["digest"] = digest,
                    ["size"] = size,
                    ["annotations"] = layerAnnotations,
                },
            },
            ["annotations"] = layerAnnotations.DeepClone(),
        };

        return Encoding.UTF8.GetBytes(
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
        );
    }

    private static OciManifest ParseManifest(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (
            root.TryGetProperty("mediaType", out var mediaType)
            && mediaType.GetString() is { } value
            && value.Contains("index", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException(
                "OCI artifact is a multi-platform index; only single-layer artifacts are supported."
            );
        }

        var config = root.GetProperty("config");
        var layers = root.GetProperty("layers");
        if (layers.GetArrayLength() == 0)
        {
            throw new InvalidDataException("OCI artifact contains no layers.");
        }

        var parsedLayers = new List<OciDescriptor>(layers.GetArrayLength());
        foreach (var layer in layers.EnumerateArray())
        {
            parsedLayers.Add(
                new OciDescriptor(
                    layer.GetProperty("mediaType").GetString() ?? string.Empty,
                    layer.GetProperty("digest").GetString() ?? string.Empty,
                    layer.GetProperty("size").GetInt64()
                )
            );
        }

        return new OciManifest(
            root.TryGetProperty("mediaType", out var rootMediaType)
                ? rootMediaType.GetString() ?? OciNames.ManifestMediaType
                : OciNames.ManifestMediaType,
            new OciDescriptor(
                config.GetProperty("mediaType").GetString() ?? string.Empty,
                config.GetProperty("digest").GetString() ?? string.Empty,
                config.GetProperty("size").GetInt64()
            ),
            parsedLayers
        );
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string scope,
        CancellationToken cancellationToken,
        bool allowRetry = true
    )
    {
        var request = requestFactory();
        HttpResponseMessage response;
        try
        {
            if (_tokens.TryGetValue(scope, out var cached))
            {
                request.Headers.Authorization = cached;
            }

            response = await httpClient.SendAsync(request, cancellationToken);
        }
        finally
        {
            // The request owns its content; disposing it releases streamed upload files.
            request.Dispose();
        }

        if (response.StatusCode != HttpStatusCode.Unauthorized || !allowRetry)
        {
            return response;
        }

        var challenge = ReadChallenge(response.Headers, scope);
        response.Dispose();
        if (challenge is null)
        {
            throw new HttpRequestException(
                $"OCI registry rejected the request for '{scope}' and returned no usable WWW-Authenticate challenge.",
                null,
                HttpStatusCode.Unauthorized
            );
        }

        _tokens[scope] = await AcquireTokenAsync(challenge, scope, cancellationToken);
        return await SendAsync(requestFactory, scope, cancellationToken, allowRetry: false);
    }

    private async Task<AuthenticationHeaderValue> AcquireTokenAsync(
        AccessChallenge challenge,
        string scope,
        CancellationToken cancellationToken
    )
    {
        if (string.Equals(challenge.Scheme, BasicRealm, StringComparison.OrdinalIgnoreCase))
        {
            if (credential is null)
            {
                throw new InvalidOperationException(
                    $"OCI registry '{httpClient.BaseAddress}' requires credentials."
                );
            }

            return BasicAuthorization();
        }

        var builder = new StringBuilder(BuildTokenUrl(challenge.Realm));
        builder.Append('?').Append("scope=").Append(Uri.EscapeDataString(scope));
        if (!string.IsNullOrWhiteSpace(challenge.Service))
        {
            builder.Append("&service=").Append(Uri.EscapeDataString(challenge.Service));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.ToString());
        if (credential is not null)
        {
            request.Headers.Authorization = BasicAuthorization();
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        foreach (var property in new[] { "token", "access_token" })
        {
            if (root.TryGetProperty(property, out var token) && token.GetString() is { Length: > 0 } value)
            {
                return new AuthenticationHeaderValue("Bearer", value);
            }
        }

        throw new InvalidDataException("OCI token endpoint returned no token.");
    }

    private AuthenticationHeaderValue BasicAuthorization() =>
        new(
            BasicRealm,
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credential!.Username}:{credential.Password}")
            )
        );

    /// <summary>
    /// Registries advertise the token realm as an absolute URL (for example
    /// <c>https://ghcr.io/token</c>), but a bare host is also tolerated.
    /// </summary>
    private static string BuildTokenUrl(string realm)
    {
        var trimmed = (realm ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0 || !trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed;
    }

    private AccessChallenge? ReadChallenge(HttpResponseHeaders headers, string scope)
    {
        if (headers.WwwAuthenticate.Count == 0)
        {
            return null;
        }

        var header = headers.WwwAuthenticate.FirstOrDefault(item =>
            string.Equals(item.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Scheme, BasicRealm, StringComparison.OrdinalIgnoreCase)
        );
        if (header is null)
        {
            return null;
        }

        if (!_challenges.TryGetValue(scope, out var cached) || cached.Scheme != header.Scheme)
        {
            cached = AccessChallenge.Parse(header);
            _challenges[scope] = cached;
        }

        return cached;
    }

    private static Uri AppendDigest(Uri uploadUri, string digest)
    {
        var query = uploadUri.Query.TrimStart('?');
        var separator = query.Length == 0 ? string.Empty : "&";
        return new Uri(
            $"{uploadUri.GetLeftPart(UriPartial.Path)}?{query}{separator}digest={Uri.EscapeDataString(digest)}"
        );
    }

    private static async Task<string> ComputeFileDigestAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"OCI request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}",
            null,
            response.StatusCode
        );
    }

    private sealed record AccessChallenge(string Scheme, string Realm, string? Service)
    {
        public static AccessChallenge Parse(AuthenticationHeaderValue header)
        {
            if (string.Equals(header.Scheme, BasicRealm, StringComparison.OrdinalIgnoreCase))
            {
                return new AccessChallenge(BasicRealm, string.Empty, null);
            }

            var parameters = header.Parameter ?? string.Empty;
            return new AccessChallenge("Bearer", Read(parameters, "realm"), Read(parameters, "service"));
        }

        private static string Read(string parameters, string key)
        {
            foreach (var part in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                if (
                    part[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return part[(separator + 1)..].Trim().Trim('"');
                }
            }

            return string.Empty;
        }
    }
}
