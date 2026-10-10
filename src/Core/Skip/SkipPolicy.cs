using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Skip;

/// <summary>Why a repo is scanned or skipped for a tool.</summary>
public enum ScanReason
{
    /// <summary>Nothing relevant changed since the last clean scan. The only reason a repo is skipped.</summary>
    Unchanged,

    SkipDisabled,
    FullMode,
    ForceFull,
    FullRescanDue,
    NeverSkip,
    NoStoredState,
    UnknownKey,
    KeyChanged,

    /// <summary>Skippable, but picked at random to be scanned anyway to catch changes the key cannot see.</summary>
    Canary
}

public sealed record SkipDecision(bool Skip, ScanReason Reason, bool Canary = false);

/// <summary>What the policy needs to know about a tool for one run. The same for every repo.</summary>
public sealed record SkipRules(
    bool Enabled,
    ScanMode Mode,
    bool ForceFull,
    bool NeverSkip,
    bool FullRescanDue,
    int CanaryPercent,
    ToolScanKey Key)
{
    /// <summary>
    /// A tool scans every repo once a scheduled full rescan is due. One that has never finished a full pass counts
    /// as due, since nothing says its stored scans are complete.
    /// </summary>
    public static bool IsFullRescanDue(ToolInventoryState state, int everyDays, DateTimeOffset now) =>
        state.LastFullScanAt is not { } last || now - last > TimeSpan.FromDays(everyDays);

    /// <summary>The rules for one tool in this run, from engine.json, the request and the saved scan state.</summary>
    public static SkipRules Create(RunContext context, string tool, DateTimeOffset now)
    {
        var settings = context.Config.Current;
        var toolState = context.FoundState.ToolState(tool);

        return new SkipRules(
            Enabled: settings.Skip.Enabled,
            Mode: context.Request.Mode,
            ForceFull: toolState.ForceFull,
            NeverSkip: ToolScanKey.NeverSkip(tool, settings.Tool(tool)),
            FullRescanDue: IsFullRescanDue(toolState, settings.Skip.FullRescanEveryDays, now),
            CanaryPercent: context.Request.Skip.CanaryPercent ?? settings.Skip.CanaryPercent,
            Key: ToolScanKey.For(context.Config, tool));
    }
}

public static class SkipPolicy
{
    /// <summary>
    /// Scan or skip, before canaries are picked. Where there is any doubt the answer is scan: a missing or unknown
    /// part of the key, a SHA that could not be read, no earlier scan, or a part that changed.
    /// </summary>
    public static SkipDecision Classify(SkipRules rules, RepoOutcome repo, RepositoryScanState? stored)
    {
        if (!rules.Enabled)
        {
            return Scan(ScanReason.SkipDisabled);
        }

        if (rules.Mode == ScanMode.Full)
        {
            return Scan(ScanReason.FullMode);
        }

        if (rules.ForceFull)
        {
            return Scan(ScanReason.ForceFull);
        }

        if (rules.NeverSkip)
        {
            return Scan(ScanReason.NeverSkip);
        }

        if (rules.FullRescanDue)
        {
            return Scan(ScanReason.FullRescanDue);
        }

        if (stored is null)
        {
            return Scan(ScanReason.NoStoredState);
        }

        if (!rules.Key.IsComplete || string.IsNullOrWhiteSpace(repo.HeadSha) || string.IsNullOrWhiteSpace(stored.Sha))
        {
            return Scan(ScanReason.UnknownKey);
        }

        return rules.Key.Matches(stored, repo.HeadSha)
            ? new SkipDecision(Skip: true, ScanReason.Unchanged)
            : Scan(ScanReason.KeyChanged);
    }

    private static SkipDecision Scan(ScanReason reason) => new(Skip: false, reason);
}

/// <summary>Decisions for every repo a tool will look at in one run.</summary>
public sealed class SkipPlan
{
    private readonly Dictionary<string, SkipDecision> _decisions;
    private readonly Dictionary<string, ToolSkippedMarker> _skipMarkers;

    public SkipPlan(Dictionary<string, SkipDecision> decisions, Dictionary<string, ToolSkippedMarker> skipMarkers)
    {
        _decisions = decisions;
        _skipMarkers = skipMarkers;
    }

    /// <summary>Repos chosen to be scanned although they were skippable.</summary>
    public int CanaryCount => _decisions.Values.Count(d => d.Canary);

    /// <summary>Repos that will not be scanned because nothing changed.</summary>
    public int UnchangedCount => _skipMarkers.Count;

    /// <summary>The decision for a repo. A repo the plan did not cover is scanned.</summary>
    public SkipDecision DecisionFor(string repoKey) =>
        _decisions.GetValueOrDefault(repoKey) ?? new SkipDecision(Skip: false, ScanReason.NoStoredState);

    /// <summary>The marker to write if this repo is skipped as unchanged, otherwise null.</summary>
    public ToolSkippedMarker? SkipMarkerFor(string repoKey) => _skipMarkers.GetValueOrDefault(repoKey);
}

public static class SkipPlanner
{
    /// <summary>
    /// Decides for every repo. Then, of the repos that could be skipped, a random <paramref name="rules"/>.CanaryPercent
    /// are scanned anyway. At least one if the percentage is above zero and anything is skippable.
    /// </summary>
    public static SkipPlan Plan(
        IReadOnlyList<RepoOutcome> repos,
        SkipRules rules,
        Func<string, RepositoryScanState?> storedFor,
        Random random)
    {
        var decisions = new Dictionary<string, SkipDecision>(StringComparer.Ordinal);
        var skippable = new List<RepoOutcome>();

        foreach (var repo in repos.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var decision = SkipPolicy.Classify(rules, repo, storedFor(repo.Key));
            decisions[repo.Key] = decision;
            if (decision.Skip)
            {
                skippable.Add(repo);
            }
        }

        foreach (var repo in PickCanaries(skippable, rules.CanaryPercent, random))
        {
            decisions[repo.Key] = new SkipDecision(Skip: false, ScanReason.Canary, Canary: true);
        }

        var markers = new Dictionary<string, ToolSkippedMarker>(StringComparer.Ordinal);
        foreach (var repo in skippable.Where(r => decisions[r.Key].Skip))
        {
            var stored = storedFor(repo.Key)!;
            markers[repo.Key] = new ToolSkippedMarker
            {
                Reason = "Unchanged",
                Sha = repo.HeadSha,
                Key = rules.Key.ToMarkerKey(),
                LastScannedAt = stored.ScannedAt
            };
        }

        return new SkipPlan(decisions, markers);
    }

    // A partial shuffle, so each skippable repo is equally likely to be picked.
    private static IEnumerable<RepoOutcome> PickCanaries(List<RepoOutcome> skippable, int percent, Random random)
    {
        var count = (int)Math.Ceiling(skippable.Count * Math.Clamp(percent, 0, 100) / 100.0);
        for (var i = 0; i < count; i++)
        {
            var pick = random.Next(i, skippable.Count);
            (skippable[i], skippable[pick]) = (skippable[pick], skippable[i]);
            yield return skippable[i];
        }
    }
}
