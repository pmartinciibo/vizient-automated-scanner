using System.Security.Cryptography;
using System.Text;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Skip;

/// <summary>
/// The parts of the skip key that belong to the tool rather than the repo (design section 11): how it scans, which
/// version of it, which rules, and a hash of its settings. The fourth part, the HEAD SHA, comes from the repo.
/// A part left blank is unknown, and an unknown part always means scan.
/// </summary>
public sealed record ToolScanKey(string ScanMode, string ToolVersion, string RulesVersion, string ConfigHash)
{
    public const string DefaultScanMode = "worktree";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ScanMode)
        && !string.IsNullOrWhiteSpace(ToolVersion)
        && !string.IsNullOrWhiteSpace(RulesVersion)
        && !string.IsNullOrWhiteSpace(ConfigHash);

    /// <summary>True only if every part, including the SHA, equals what the last clean scan recorded.</summary>
    public bool Matches(RepositoryScanState stored, string sha) =>
        string.Equals(stored.Sha, sha, StringComparison.Ordinal)
        && string.Equals(stored.ScanMode, ScanMode, StringComparison.Ordinal)
        && string.Equals(stored.ToolVersion, ToolVersion, StringComparison.Ordinal)
        && string.Equals(stored.RulesVersion, RulesVersion, StringComparison.Ordinal)
        && string.Equals(stored.ConfigHash, ConfigHash, StringComparison.Ordinal);

    public SkipKey ToMarkerKey() =>
        new() { ScanMode = ScanMode, ToolVersion = ToolVersion, RulesVersion = RulesVersion, ConfigHash = ConfigHash };

    /// <summary>
    /// Builds the key from engine.json. The version, rules version and mode are the pinned values written there
    /// (the engine does not ask the tool), so changing the tool without changing them does not change the key.
    /// </summary>
    public static ToolScanKey For(EngineConfig config, string tool)
    {
        var settings = config.Current.Tool(tool);
        return new ToolScanKey(
            settings.Mode ?? DefaultScanMode,
            settings.Version ?? string.Empty,
            settings.RulesVersion ?? string.Empty,
            ComputeConfigHash(config, settings, tool));
    }

    /// <summary>
    /// A hash of what can change what a tool scans or finds: its mode, whether it verifies, its rule packs, and the
    /// shared exclusions file. Settings that only change how it runs (parallelism, timeout, secret names) are left
    /// out, so tuning them does not force a rescan of everything.
    /// </summary>
    public static string ComputeConfigHash(EngineConfig config, ToolSettings settings, string? tool = null)
    {
        var text = new StringBuilder()
            .Append("mode=").Append(settings.Mode ?? DefaultScanMode).Append('\n')
            .Append("verify=").Append(settings.Verify?.ToString() ?? "unset").Append('\n')
            .Append("packs=").Append(string.Join('|', (settings.Packs ?? []).Order(StringComparer.Ordinal))).Append('\n')
            .Append("exclusions=").Append(ExclusionsText(config)).Append('\n');

        if (tool == ToolNames.SonarDotNet)
        {
            text.Append("buildStrategy=project-aware-v1\n")
                .Append("dotnetPath=").Append(settings.DotnetPath ?? "dotnet").Append('\n')
                .Append("msbuildPath=").Append(settings.MsbuildPath ?? "auto").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// True for the runs that must never be skipped: Snyk in dependency mode (new CVEs appear without a code change)
    /// and TruffleHog with verification on (a secret's verified status can change without a commit).
    /// </summary>
    public static bool NeverSkip(string tool, ToolSettings settings) =>
        (tool == ToolNames.Snyk && settings.Mode == "dependencies")
        || (tool == ToolNames.TruffleHog && settings.Verify == true);

    // Line endings and trailing spaces are not changes to the exclusions.
    private static string ExclusionsText(EngineConfig config)
    {
        if (config.ExclusionsFile is null)
        {
            return "none";
        }

        var lines = File.ReadAllText(config.ExclusionsFile)
            .Split('\n')
            .Select(line => line.TrimEnd());
        return string.Join('\n', lines).TrimEnd();
    }
}
