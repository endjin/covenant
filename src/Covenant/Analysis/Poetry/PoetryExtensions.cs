namespace Covenant.Analysis.Npm;

internal static class PoetryExtensions
{
    public static BomComponent? FindPoetryComponent(this IReadOnlyGraph<BomComponent> graph, string name, PoetryVersionRange range, out bool foundMatch)
    {
        var nodes = graph.Nodes.OfType<PoetryComponent>()
            .Where(c => c.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();

        var version = range.Matches(nodes.Select(c => c.Data));
        if (version == null)
        {
            foundMatch = false;

            if (nodes.Length == 1)
            {
                return nodes[0];
            }
        }

        foundMatch = true;
        return graph.Nodes.OfType<PoetryComponent>()
            .Where(c => c.Name.Equals(name, StringComparison.Ordinal))
            .FirstOrDefault(x => x.Data is PoetryVersion v && v.Equals(version));
    }
}
