using System.Text.RegularExpressions;

namespace Vizient.AutomatedScanner.Core.IO;

/// <summary>Names that come from outside (runId, repo keys) become file and folder names, so keep them plain.</summary>
public static partial class SafeName
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);
}
