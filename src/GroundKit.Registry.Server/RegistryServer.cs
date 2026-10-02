using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace GroundKit.Registry.Server;

public static class RegistryServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var databasePath =
            Environment.GetEnvironmentVariable("REGISTRY_DATABASE_PATH") ?? "/data/registry.db";
        var bucket = Environment.GetEnvironmentVariable("MINIO_BUCKET") ?? "groundkit-packages";
        var endpoint =
            Environment.GetEnvironmentVariable("MINIO_ENDPOINT") ?? "http://localhost:9000";
        var accessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "minioadmin";
        var secretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "minioadmin";
        var publishKey = Environment.GetEnvironmentVariable("REGISTRY_PUBLISH_KEY")?.Trim();

        var databaseDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        var database = new SqliteConnection($"Data Source={databasePath}");
        await database.OpenAsync();
        await using (var command = database.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS packages (
                    registry TEXT NOT NULL,
                    name TEXT NOT NULL,
                    version TEXT NOT NULL,
                    description TEXT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    source_commit TEXT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (registry, name, version)
                )
                """;
            await command.ExecuteNonQueryAsync();
        }

        var client = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true }
        );
        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception exception)
            when (exception.StatusCode == System.Net.HttpStatusCode.Conflict) { }

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet(
            "/search",
            async (HttpRequest request, CancellationToken cancellationToken) =>
            {
                var registry = request.Query["registry"].ToString();
                var name = request.Query["name"].ToString();
                var version = request.Query["version"].ToString();
                await using var command = database.CreateCommand();
                command.CommandText = """
                SELECT registry, name, version, description, size
                FROM packages
                WHERE ($registry = '' OR registry = $registry)
                  AND ($name = '' OR name = $name)
                  AND ($version = '' OR version = $version)
                ORDER BY registry, name, version
                """;
                command.Parameters.AddWithValue("$registry", registry);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$version", version);
                var results = new List<object>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(
                        new
                        {
                            registry = reader.GetString(0),
                            name = reader.GetString(1),
                            version = reader.GetString(2),
                            description = reader.IsDBNull(3) ? null : reader.GetString(3),
                            size = reader.GetInt64(4),
                        }
                    );
                }
                return Results.Ok(results);
            }
        );

        app.MapGet(
            "/packages/{registry}/{name}/{version}",
            async (
                string registry,
                string name,
                string version,
                CancellationToken cancellationToken
            ) => await MetadataAsync(database, registry, name, version, cancellationToken)
        );
        app.MapGet(
            "/packages/{registry}/{name}/{version}/download",
            async (
                string registry,
                string name,
                string version,
                CancellationToken cancellationToken
            ) =>
            {
                var metadata = await FindAsync(
                    database,
                    registry,
                    name,
                    version,
                    cancellationToken
                );
                if (metadata is null)
                    return Results.NotFound();
                var response = await client.GetObjectAsync(
                    new GetObjectRequest { BucketName = bucket, Key = metadata.Value.ObjectKey },
                    cancellationToken
                );
                return Results.Stream(
                    response.ResponseStream,
                    "application/octet-stream",
                    $"{name}@{version}.db"
                );
            }
        );
        app.MapPost(
            "/packages/{registry}/{name}/{version}",
            async (
                string registry,
                string name,
                string version,
                HttpRequest request,
                CancellationToken cancellationToken
            ) =>
            {
                if (
                    string.IsNullOrWhiteSpace(publishKey)
                    || request.Headers.Authorization != $"Bearer {publishKey}"
                )
                    return Results.Unauthorized();
                await using var body = new MemoryStream();
                await request.Body.CopyToAsync(body, cancellationToken);
                var bytes = body.ToArray();
                var sha256 = Convert
                    .ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant();
                var objectKey = $"{registry}/{name}/{version}/package.db";
                try
                {
                    await client.PutObjectAsync(
                        new PutObjectRequest
                        {
                            BucketName = bucket,
                            Key = objectKey,
                            InputStream = new MemoryStream(bytes),
                            ContentType = "application/octet-stream",
                        },
                        cancellationToken
                    );
                    await using var command = database.CreateCommand();
                    command.CommandText =
                        "INSERT INTO packages(registry,name,version,size,sha256,object_key,created_at) VALUES($registry,$name,$version,$size,$sha256,$objectKey,$createdAt)";
                    command.Parameters.AddWithValue("$registry", registry);
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$version", version);
                    command.Parameters.AddWithValue("$size", bytes.LongLength);
                    command.Parameters.AddWithValue("$sha256", sha256);
                    command.Parameters.AddWithValue("$objectKey", objectKey);
                    command.Parameters.AddWithValue(
                        "$createdAt",
                        DateTimeOffset.UtcNow.ToString("O")
                    );
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    return Results.Conflict(new { message = "Package version already exists." });
                }
                return Results.Created(
                    $"/packages/{registry}/{name}/{version}",
                    new
                    {
                        registry,
                        name,
                        version,
                        size = bytes.LongLength,
                        sha256,
                    }
                );
            }
        );

        await app.RunAsync();
        return 0;
    }

    private static async Task<IResult> MetadataAsync(
        SqliteConnection database,
        string registry,
        string name,
        string version,
        CancellationToken cancellationToken
    )
    {
        var metadata = await FindAsync(database, registry, name, version, cancellationToken);
        return metadata is null ? Results.NotFound() : Results.Ok(metadata.Value.Value);
    }

    private static async Task<(object Value, string ObjectKey)?> FindAsync(
        SqliteConnection database,
        string registry,
        string name,
        string version,
        CancellationToken cancellationToken
    )
    {
        await using var command = database.CreateCommand();
        command.CommandText =
            "SELECT registry,name,version,description,size,sha256,object_key,source_commit,created_at FROM packages WHERE registry=$registry AND name=$name AND version=$version";
        command.Parameters.AddWithValue("$registry", registry);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var objectKey = reader.GetString(6);
        return (
            new
            {
                registry = reader.GetString(0),
                name = reader.GetString(1),
                version = reader.GetString(2),
                description = reader.IsDBNull(3) ? null : reader.GetString(3),
                size = reader.GetInt64(4),
                sha256 = reader.GetString(5),
                sourceCommit = reader.IsDBNull(7) ? null : reader.GetString(7),
                createdAt = reader.GetString(8),
            },
            objectKey
        );
    }
}