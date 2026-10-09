using GroundKit.Core.Contracts;

namespace GroundKit.Semantic;

public interface ISemanticSearchContext
{
    SearchMode Mode { get; }
    SemanticModelProfile Model { get; }
    string CachePath { get; }
}