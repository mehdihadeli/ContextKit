using GroundKit.Configuration;
using GroundKit.Ingestion.DependencyInjection;
using GroundKit.Storage.DependencyInjection;
using GroundKit.Storage.Sqlite;
using GroundKit.Semantic;
using Microsoft.Extensions.DependencyInjection;

namespace GroundKit.Hosting;

public static class GroundKitServiceCollectionExtensions
{
    public static IServiceCollection AddGroundKitServices(this IServiceCollection services)
    {
        services.AddSingleton(GroundKitOptions.Load());
        services.AddSingleton<SemanticRuntime>();
        services.AddSingleton<ISemanticEmbeddingProvider>(provider => provider.GetRequiredService<SemanticRuntime>());
        services.AddSingleton<ISemanticSearchContext>(provider => provider.GetRequiredService<SemanticRuntime>());
        services.AddSingleton<SemanticSearchIndex>();
        return services.AddGroundKitIngestion().AddGroundKitStorage();
    }
}
