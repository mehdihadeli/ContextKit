using GroundKit.Core.Contracts;

namespace GroundKit.Semantic;

public sealed record SemanticSettings(SearchMode Mode = SearchMode.Lexical, string Model = "bge-micro-v2");

public sealed record SemanticAsset(string Name, string Url, string Sha256, long Size);

public sealed record SemanticModelProfile(
    string Id,
    string Revision,
    int Dimensions,
    int MaxTokens,
    string License,
    IReadOnlyList<SemanticAsset> Assets
)
{
    public string Identity => $"{Id}:{Revision}:{Dimensions}:{MaxTokens}:int8-mean-l2-wordpiece-v1";
}