using GroundKit.Configuration;
using GroundKit.Core.Abstractions;
using GroundKit.Ingestion.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GroundKit.Ingestion.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroundKitIngestion(this IServiceCollection services)
    {
        services.AddHttpClient(
            "groundkit",
            (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<GroundKitOptions>();
                client.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
                AddConfiguredHeaders(client, options);
            }
        );
        services.AddHttpClient<IContextRegistryClient, ContextRegistryClient>(
            (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<GroundKitOptions>();
                client.BaseAddress = new Uri(
                    (options.RegistryUrl ?? ContextRegistryClient.ResolveBaseUrl()) + "/"
                );
                client.Timeout = TimeSpan.FromMinutes(5);
                AddConfiguredHeaders(client, options);
            }
        );
        // OCI pulls target a per-artifact registry host, so the base address is assigned by the
        // caller while the handler and configured headers come from this named client.
        services.AddHttpClient(
            "groundkit-oci",
            (serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<GroundKitOptions>();
                client.Timeout = TimeSpan.FromMinutes(10);
                AddConfiguredHeaders(client, options);
            }
        );
        services.AddSingleton<ISourceDetector, SourceDetector>();
        services.AddSingleton<IGitReferenceProvider, GitReferenceProvider>();
        services.AddSingleton<IDocumentPackageBuilder, DocumentPackageBuilder>();
        services.AddSingleton<IPackageDownloadService, PackageDownloadService>();
        return services;
    }

    private static void AddConfiguredHeaders(HttpClient client, GroundKitOptions options)
    {
        if (options.HttpHeaders is null)
        {
            return;
        }

        foreach (var header in options.HttpHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
