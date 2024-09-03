namespace Covenant.Analysis.Poetry;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tomlyn.Model;

internal class PoetryAnalyzer(IFileSystem fileSystem, IEnvironment environment) : Analyzer
{
    private const string ExcludeGroups = "--exclude-poetry-groups";
    private const string DisablePoetry = "--disable-poetry";
    private const string VirtualEnvironmentPath = "--virtual-environment-path";

    private readonly PoetryAssetReader _assetReader = new PoetryAssetReader(fileSystem, environment);
    private readonly IEnvironment _environment = environment;
    private bool _enabled = true;
    private DirectoryPath? _virtualEnvironmentPath;

    public override bool Enabled => _enabled;
    public override string[] Patterns { get; } = ["**/pyproject.toml"];

    public override void Analyze(AnalysisContext context, FilePath path)
    {
        path = path.GetDirectory().CombineWithFilePath("pyproject.toml");

        // Read the asset file
        var assetFile = _assetReader.ReadAssetFile(path);
        if (assetFile == null)
        {
            context.AddError("Could not read pyproject.toml");
            return;
        }

        // Add main component
        var root = context.AddComponent(
            new PoetryComponent(assetFile.Tool.Poetry.Name!, assetFile.Tool.Poetry.Version!, BomComponentKind.Root));

        // Read the lock file
        var lockPath = path.GetDirectory().CombineWithFilePath("poetry.lock");
        var lockFile = _assetReader.ReadLockFile(lockPath);
        if (lockFile == null)
        {
            context.AddError("Could not read poetry.lock");
            return;
        }

        // Add all packages
        if (lockFile.Packages != null)
        {
            foreach (var package in lockFile.Packages)
            {
                if (package.Name is null || package.Version is null)
                {
                    continue;
                }

                var sitePackagesPath = string.Empty;
                const string DirectoryNameToFind = "site-packages";
                var sitePackageDirs = Directory.EnumerateDirectories(_virtualEnvironmentPath!.FullPath, DirectoryNameToFind, SearchOption.AllDirectories).ToList();
                if (sitePackageDirs.Count > 1)
                {
                    context.AddError("Found multiple 'site-packages', but no version was specified");
                    return;
                }
                else
                {
                    sitePackagesPath = sitePackageDirs[0];
                }

                var license = PoetryLicenseParser.Parse(sitePackagesPath, package, context);

                // NOTE: We might be interested in dealing with the 'extras' dependencies differently here? (e.g. as per NPM OptionalDependencies)

                // Since packages can have a varying number of files and file types, the SBOM will use a hash calculated from the combination
                // of all the indivdual file hashes associated with the package.
                var hash = string.Empty;
                if (package.Files != null && package.Files.All(f => f.Hash != null))
                {
                    var combinedHash = package.Files.Select(f => f.Hash).Aggregate((a, b) => a + b);
                    var hashBytes = Encoding.UTF8.GetBytes(combinedHash);
                    using var sha256 = SHA256.Create();
                    var hashalg = sha256.ComputeHash(hashBytes);
                    hash = $"sha256:{Convert.ToHexString(hashalg)}".ToLower();
                }

                context
                    .AddComponent(
                        new PoetryComponent(package.Name, package.Version, BomComponentKind.Library))
                    .SetHash(PoetryHashParser.Parse(hash))
                    .SetLicense(license);
            }
        }

        // If we got errors, then abort
        if (context.HasErrors)
        {
            return;
        }

        // Add dependencies to the main project
        if (assetFile.Tool.Poetry.Dependencies != null)
        {
            AddDependencies(context, root, lockFile, assetFile.Tool.Poetry.Dependencies);
        }

        if (assetFile.Tool.Poetry.Groups != null)
        {
            foreach (var (groupName, group) in assetFile.Tool.Poetry.Groups)
            {
                if (context.Cli.GetOption<string[]>(ExcludeGroups)!.Contains(groupName))
                {
                    continue;
                }

                if (group.Dependencies != null)
                {
                    AddDependencies(context, root, lockFile, group.Dependencies);
                }
            }
        }
    }

    public override void BeforeAnalysis(AnalysisSettings settings)
    {
        if (settings.Cli.GetOption<bool>(DisablePoetry))
        {
            _enabled = false;
        }

        _virtualEnvironmentPath = new DirectoryPath(settings.Cli.GetOption<string>(VirtualEnvironmentPath)).MakeAbsolute(_environment);
    }

    public override bool CanHandle(AnalysisContext context, FilePath path)
    {
        return path.GetFilename().FullPath.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            path.GetFilename().FullPath.Equals("poetry.lock", StringComparison.OrdinalIgnoreCase);
    }

    public override void Initialize(ICommandLineAugmentor cli)
    {
        cli.AddOption<string[]>(ExcludeGroups, "Excludes specified dependency groups for Python Poetry projects", Array.Empty<string>());
        cli.AddOption<bool>(DisablePoetry, "Disables the Python Poetry analyzer", false);
        cli.AddOption<string>(VirtualEnvironmentPath, "The path to the Python virtual environment", string.Empty);
    }

    public override bool ShouldTraverse(DirectoryPath path)
    {
        return !path.FullPath.StartsWith(_virtualEnvironmentPath.FullPath);
    }

    private static void AddDependencies(AnalysisContext context, BomComponent parent, PoetryLock poetryLock, Dictionary<string, object>? dependencies)
    {
        if (dependencies != null)
        {
            foreach (var (packageName, packageVersion) in dependencies)
            {
                string? versionRange = null;

                // Package version could either be a string (e.g. ">=1.0.0") or a dictionary (e.g. { "version": ">=1.0.0" })
                if (packageVersion is Dictionary<string, string> versionDict)
                {
                    if (!versionDict.TryGetValue("version", out versionRange))
                    {
                        context.AddWarning($"Could not find version for Poetry package [yellow]{packageName}[/]");
                        continue;
                    }
                }
                else if (packageVersion is TomlTable table)
                {
                    if (table.ContainsKey("version"))
                    {
                        versionRange = table["version"].ToString();
                    }
                    else
                    {
                        context.AddWarning($"Could not find version for Poetry package [yellow]{packageName}[/]");
                        continue;
                    }
                }
                else if (packageVersion is string)
                {
                    versionRange = packageVersion.ToString();
                }

                if (versionRange is null)
                {
                    context.AddWarning($"Could not find version for Poetry package [yellow]{packageName}[/]");
                    continue;
                }

                // Find the component
                var bomComponent = context.Graph.FindPoetryComponent(packageName, new PoetryVersionRange(versionRange), out var foundMatch);
                if (bomComponent == null)
                {
                    context.AddWarning($"Could not find Poetry package [yellow]{packageName}[/]");

                    continue;
                }

                var childPackage = poetryLock.Packages!.Find(p => p.Name == packageName && p.Version == bomComponent.Version);

                AddDependencies(context, bomComponent, poetryLock, childPackage?.Dependencies);
                context.Connect(parent, bomComponent);
            }
        }
    }
}
