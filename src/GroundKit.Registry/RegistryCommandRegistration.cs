using Spectre.Console.Cli;

namespace GroundKit.Registry;

public static class RegistryCommandRegistration
{
    public static void Configure(IConfigurator config)
    {
        config.AddBranch<CommandSettings>("registry", registry =>
        {
            registry.AddCommand<CatalogIndexRegistryCommand>("catalog-index")
                .WithDescription("Generate a static registry catalog from built packages, served from OCI artifacts or Release assets.");
            registry
                .AddCommand<ListRegistryCommand>("list")
                .WithAlias("l")
                .WithAlias("ls")
                .WithDescription("List registry definitions from a directory.")
                .WithExample("registry", "ls", "-d", "registry");
            registry
                .AddCommand<ValidateRegistryCommand>("validate")
                .WithAlias("v")
                .WithAlias("val")
                .WithDescription("Validate registry definition files.")
                .WithExample("registry", "v", "-d", "registry");
            registry
                .AddCommand<BuildRegistryCommand>("build")
                .WithAlias("b")
                .WithDescription("Build one package from a registry definition.")
                .WithExample("registry", "b", "react", "-d", "registry", "-o", "./dist-packages");
            registry
                .AddCommand<BuildAllRegistryCommand>("build-all")
                .WithAlias("ba")
                .WithDescription("Build all registry definitions into package artifacts.")
                .WithExample("registry", "ba", "-d", "registry", "-o", "./dist-packages");
            registry
                .AddCommand<PushOciRegistryCommand>("push-oci")
                .WithAlias("po")
                .WithDescription("Push built packages to an OCI registry and record their digests.")
                .WithExample(
                    "registry",
                    "po",
                    "-o",
                    "./dist-packages",
                    "--oci-repository",
                    "ghcr.io/owner/groundkit",
                    "--oci-references",
                    "oci-references.json"
                );
            registry
                .AddCommand<BundleRegistryCommand>("bundle")
                .WithAlias("bd")
                .WithAlias("bun")
                .WithDescription("Bundle built package artifacts into zip or tar.gz.")
                .WithExample("registry", "bd", "-o", "./dist-packages", "-f", "zip");
            registry
                .AddCommand<ImportBundleRegistryCommand>("import-bundle")
                .WithAlias("ib")
                .WithDescription("Extract a registry bundle into local package artifacts.")
                .WithExample(
                    "registry",
                    "ib",
                    "./dist-packages/groundkit-registry.zip",
                    "-o",
                    "./imported-packages"
                );
        });
    }
}
