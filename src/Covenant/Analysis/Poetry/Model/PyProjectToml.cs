using System.Runtime.Serialization;

namespace Covenant.Analysis.Poetry;
internal sealed class PyProjectToml
{
    public PyProjectTool? Tool { get; set; }
}

internal sealed class PyProjectTool
{
    public PyProjectToolPoetry? Poetry { get; set; }
}

internal sealed class PyProjectToolPoetry
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public Dictionary<string, object>? Dependencies { get; set; }

    [DataMember(Name = "group")]
    public Dictionary<string, PyProjectToolPoetryGroupDependencies>? Groups { get; set; }
}

internal sealed class PyProjectToolPoetryGroupDependencies
{
    [DataMember(Name = "dependencies")]
    public Dictionary<string, object>? Dependencies { get; set; }
}
