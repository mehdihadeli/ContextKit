using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Connectors.Onnx;

#pragma warning disable SKEXP0001, SKEXP0070

const int protocolVersion = 1;
if (args.Length != 1)
{
    Console.Error.WriteLine("Expected model directory.");
    return 1;
}

var services = new ServiceCollection();
services.AddBertOnnxEmbeddingGenerator(
    Path.Combine(args[0], "model.onnx"),
    Path.Combine(args[0], "vocab.txt"),
    new BertOnnxOptions
    {
        MaximumTokens = 512,
        PoolingMode = EmbeddingPoolingMode.Mean,
        NormalizeEmbeddings = true,
    }
);
using var serviceProvider = services.BuildServiceProvider();
var generator = serviceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    try
    {
        var request = JsonSerializer.Deserialize<EmbeddingRequest>(line)
            ?? throw new InvalidDataException("Missing embedding request.");
        if (request.ProtocolVersion != protocolVersion) throw new InvalidDataException("Protocol version mismatch.");
        if (request.Texts is null || request.Texts.Length is < 1 or > 32 || request.Texts.Any(text => text is null || text.Length > 100_000))
            throw new InvalidDataException("Invalid embedding batch.");
        var embeddings = await generator.GenerateAsync(request.Texts.Select(text => text!));
        var vectors = embeddings.Select(embedding => embedding.Vector.ToArray()).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { ProtocolVersion = protocolVersion, Vectors = vectors }));
    }
    catch (Exception exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ProtocolVersion = protocolVersion, Error = exception.Message }));
    }
}
return 0;

internal sealed record EmbeddingRequest(int ProtocolVersion, string?[]? Texts);