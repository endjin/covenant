namespace Covenant.Analysis.Poetry;

internal static class PoetryLicenseParser
{
    public static BomLicense? Parse(string? license)
    {
        if (license == null)
        {
            return null;
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
