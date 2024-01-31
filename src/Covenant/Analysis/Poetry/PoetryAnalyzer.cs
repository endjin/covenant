namespace Covenant.Analysis.Poetry;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal class PoetryAnalyzer : Analyzer
{
    private const string NoDevDependenciesFlag = "--no-poetry-dev-dependencies";
    private const string NoTestDependenciesFlag = "--no-poetry-test-dependencies";
    private const string DisablePoetry = "--disable-poetry";
    private const string VirtualEnvironmentPath = "--virtual-environment-path";

    private readonly PoetryAssetReader _assetReader;
    private readonly IEnvironment _environment;
    private bool _enabled = true;
    private DirectoryPath _virtualEnvironmentPath;

    public override bool Enabled => _enabled;
    public override string[] Patterns { get; } = new[] { "**/pyproject.toml" };

    public PoetryAnalyzer(IFileSystem fileSystem, IEnvironment environment)
    {
        _assetReader = new PoetryAssetReader(fileSystem, environment);
        _environment = environment;
    }

    public override void AfterAnalysis(AnalysisSettings settings)
    {
        base.AfterAnalysis(settings);
    }

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

        var optionalPackages = new HashSet<string>();

        // Add all packages
        if (lockFile.Packages != null)
        {
            foreach (var package in lockFile.Packages)
            {
                var sitePackagesPath = string.Empty;
                string directoryNameToFind = "site-packages";
                List<string> sitePackageDirs = Directory.EnumerateDirectories(_virtualEnvironmentPath.FullPath, directoryNameToFind, SearchOption.AllDirectories).ToList();
                if (sitePackageDirs.Count > 1)
                {
                    context.AddError($"Found multiple 'site-packages', but no version was specified");
                    return;
                }
                else
                {
                    sitePackagesPath = sitePackageDirs.First();
                }

                string? license = null;
                var packageMetadataPath = Path.Join(sitePackagesPath, $"{package.Name}-{package.Version}.dist-info", "metadata.json");
                if (!File.Exists(packageMetadataPath))
                {
                    // TODO:
                    // - Add fallback to use regex against the METADATA file
                    // - Also some packages have a LICENSE file, whilst others a 'licenses' folder
                    context.AddWarning($"Could not find metadata.json for package {package.Name}");
                }
                else
                {
                    using (var packageMetadataReader = new StreamReader(packageMetadataPath))
                    {
                        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(packageMetadataReader.ReadToEnd());
                        if (metadata == null)
                        {
                            context.AddWarning($"Could not read metadata.json for package {package.Name}");
                        }
                        else
                        {
                            license = metadata["license"].ToString();
                        }
                    }
                }

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
                        new PoetryComponent(package.Name!, package.Version!, BomComponentKind.Library))
                    .SetHash(PoetryHashParser.Parse(hash))
                    .SetLicense(PoetryLicenseParser.Parse(license));
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
            AddDependencies(context, root, assetFile.Tool.Poetry.Dependencies, optionalPackages);
        }

        // Add dev dependencies to the main project
        if (assetFile.Tool.Poetry.Groups.Dev != null)
        {
            if (!context.Cli.GetOption<bool>(NoDevDependenciesFlag))
            {
                AddDependencies(context, root, assetFile.Tool.Poetry.Groups.Dev, optionalPackages);
            }
        }

        // Add test dependencies to the main project
        if (assetFile.Tool.Poetry.Groups.Test != null)
        {
            if (!context.Cli.GetOption<bool>(NoTestDependenciesFlag))
            {
                AddDependencies(context, root, assetFile.Tool.Poetry.Groups.Test, optionalPackages);
            }
        }
    }

    public override void BeforeAnalysis(AnalysisSettings settings)
    {
        if (settings.Cli.GetOption<bool>(DisablePoetry))
        {
            _enabled = false;
        }

        _virtualEnvironmentPath = (new DirectoryPath(settings.Cli.GetOption<string>(VirtualEnvironmentPath))).MakeAbsolute(_environment);
    }

    public override bool CanHandle(AnalysisContext context, FilePath path)
    {
        return path.GetFilename().FullPath.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            path.GetFilename().FullPath.Equals("poetry.lock", StringComparison.OrdinalIgnoreCase);
    }

    public override void Initialize(ICommandLineAugmentor cli)
    {
        cli.AddOption<bool>(NoDevDependenciesFlag, "Excludes dev dependencies for Python Poetry projects", false);
        cli.AddOption<bool>(NoTestDependenciesFlag, "Excludes test dependencies for Python Poetry projects", false);
        cli.AddOption<bool>(DisablePoetry, "Disables the Python Poetry analyzer", false);
        cli.AddOption<string>(VirtualEnvironmentPath, "The path to the Python virtual environment", string.Empty);
    }

    public override bool ShouldTraverse(DirectoryPath path)
    {
        return !path.FullPath.StartsWith(_virtualEnvironmentPath.FullPath);
    }


    private static void AddDependencies(AnalysisContext context, BomComponent root, PyProjectToolPoetryGroupDependencies? dependencies, IReadOnlySet<string> optionalPackages)
    {
        // TODO
    }
    
    private static void AddDependencies(AnalysisContext context, BomComponent root, Dictionary<string, string>? dependencies, IReadOnlySet<string> optionalPackages)
    {
        // if (dependencies != null)
        // {
        //     foreach (var (dependencyName, dependencyRange) in dependencies)
        //     {
        //         var range = new NpmVersionRange(dependencyRange);

        //         var bomDependency = context.Graph.FindNpmComponent(dependencyName, range, out var foundMatch);
        //         if (bomDependency != null)
        //         {
        //             if (!foundMatch)
        //             {
        //                 context.AddWarning($"Could not find exact NPM dependency match [yellow]{dependencyName}[/] ({dependencyRange})");
        //             }

        //             context.Connect(root, bomDependency);
        //         }
        //         else
        //         {
        //             if (optionalPackages?.Contains(dependencyName) == false)
        //             {
        //                 context.AddWarning($"Could not find NPM dependency [yellow]{dependencyName}[/] ({dependencyRange})");
        //             }
        //         }
        //     }
        // }
    }
}
