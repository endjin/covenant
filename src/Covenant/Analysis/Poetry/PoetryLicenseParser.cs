using System.Text.RegularExpressions;

namespace Covenant.Analysis.Poetry;

internal static class PoetryLicenseParser
{
    public static BomLicense? Parse(string sitePackagesPath, PoetryLockPackage package, AnalysisContext context)
    {
        var packageName = package.Name!.Replace("-", "_");
        var jsonMetadataPath = System.IO.Path.Join(sitePackagesPath, $"{packageName}-{package.Version}.dist-info", "metadata.json");
        var metadataPath = System.IO.Path.Join(sitePackagesPath, $"{packageName}-{package.Version}.dist-info", "METADATA");
        var licensePath = System.IO.Path.Join(sitePackagesPath, $"{packageName}-{package.Version}.dist-info", "LICENSE");

        List<(string Path, Func<string, string?> Parser)> metadataTypes = [
            (jsonMetadataPath, ParseJsonMetadataFile),
            (metadataPath, ParseMetadataFile),
            (licensePath, ParseLicenseFile)
        ];

        string? licenseString = null;

        foreach (var m in metadataTypes)
        {
            if (!File.Exists(m.Path))
            {
                continue;
            }

            licenseString = m.Parser(m.Path);

            if (licenseString != null)
            {
                break;
            }
        }

        if (licenseString == null)
        {
            context.AddWarning($"Could not find license for package [yellow]{package.Name}[/] ({package.Version}). Tried metadata.json, METADATA and LICENSE files.");
        }

        return Parse(licenseString);
    }

    private static string? ParseLicenseFile(string licensePath)
    {
        return File.ReadAllText(licensePath);
    }

    private static string? ParseJsonMetadataFile(string packageMetadataPath)
    {
        using (var packageMetadataReader = new StreamReader(packageMetadataPath))
        {
            var metadata = JsonConvert.DeserializeObject<Dictionary<string, object>>(packageMetadataReader.ReadToEnd());
            return metadata?["license"]?.ToString();
        }
    }

    private static string? ParseMetadataFile(string packageMetadataPath)
    {
        using var metadataReader = new StreamReader(packageMetadataPath);
        var metadata = metadataReader.ReadToEnd();

        List<string> headers = ["Classifier: License :: OSI Approved ::", "License-Expression:", "License:"];

        foreach (var header in headers)
        {
            var licenseMatch = Regex.Match(metadata, $@"{header}\s*(.+)");

            if (licenseMatch.Success)
            {
                var licenseString = licenseMatch.Groups[1].Value.Trim().Replace(" License", string.Empty);
                return licenseString;
            }
        }

        return null;
    }

    private static BomLicense? Parse(string? license)
    {
        if (license == null)
        {
            return new BomLicense
            {
                Id = "None",
            };
        }

        if (SpdxLicense.TryGetById(license, out var spdxLicense))
        {
            return new BomLicense
            {
                Id = spdxLicense.Id,
                Expression = spdxLicense.Id,
                Name = spdxLicense.Name,
            };
        }
        else if (Uri.TryCreate(license, UriKind.Absolute, out var uri))
        {
            return new BomLicense
            {
                Url = uri.AbsoluteUri,
            };
        }
        else if (SpdxExpression.IsValidExpression(license, SpdxLicenseOptions.Relaxed))
        {
            return new BomLicense
            {
                Expression = license,
            };
        }

        return new BomLicense
        {
            Id = license,
            Name = "Unknown",
        };
    }
}
