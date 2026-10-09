namespace GroundKit.Registry.Tests.Integration;

/// <summary>
/// <see cref="Spectre.Console.AnsiConsole.Console"/> is process-wide global state, so tests that
/// redirect it to capture output must not run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RegistryConsoleCollection
{
    public const string Name = "registry-console";
}
