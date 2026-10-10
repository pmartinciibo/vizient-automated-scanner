namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Skip;
using Vizient.AutomatedScanner.Core.State;

// The decision rules on their own: what the key is made of, when a repo is skipped, and how canaries are picked.
public class SkipPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T03:00:00Z");
    private static readonly ToolScanKey Key = new("worktree", "1.x", "rules-1", "hash-1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public SkipPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SkipRules Rules(
        bool enabled = true,
        ScanMode mode = ScanMode.Normal,
        bool forceFull = false,
        bool neverSkip = false,
        bool fullRescanDue = false,
        int canaryPercent = 0,
        ToolScanKey? key = null) =>
        new(enabled, mode, forceFull, neverSkip, fullRescanDue, canaryPercent, key ?? Key);

    private static RepoOutcome Repo(string key = "gh-001", string? sha = "sha-1") => new()
    {
        Key = key, Source = "GitHub", Org = "RnD-Vizient", Name = key, HeadSha = sha
    };

    private static RepositoryScanState Stored(string sha = "sha-1", ToolScanKey? key = null) => new()
    {
        Sha = sha,
        ScanMode = (key ?? Key).ScanMode,
        ToolVersion = (key ?? Key).ToolVersion,
        RulesVersion = (key ?? Key).RulesVersion,
        ConfigHash = (key ?? Key).ConfigHash,
        ScannedAt = Now.AddDays(-3),
        FindingCount = 7
    };

    // ---- the decision for one repo ----

    [Fact]
    public void ARepoWhoseWholeKeyMatchesTheLastCleanScanIsSkippedAsUnchanged()
    {
        var decision = SkipPolicy.Classify(Rules(), Repo(), Stored());

        Assert.True(decision.Skip);
        Assert.Equal(ScanReason.Unchanged, decision.Reason);
        Assert.False(decision.Canary);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("mode")]
    [InlineData("toolVersion")]
    [InlineData("rulesVersion")]
    [InlineData("configHash")]
    public void AnyOnePartOfTheKeyChangingMeansScan(string changed)
    {
        var repo = changed == "sha" ? Repo(sha: "sha-2") : Repo();
        var rules = changed switch
        {
            "mode" => Rules(key: Key with { ScanMode = "git" }),
            "toolVersion" => Rules(key: Key with { ToolVersion = "2.x" }),
            "rulesVersion" => Rules(key: Key with { RulesVersion = "rules-2" }),
            "configHash" => Rules(key: Key with { ConfigHash = "hash-2" }),
            _ => Rules()
        };

        var decision = SkipPolicy.Classify(rules, repo, Stored());

        Assert.False(decision.Skip);
        Assert.Equal(ScanReason.KeyChanged, decision.Reason);
    }

    [Fact]
    public void ARepoNeverScannedBeforeIsScanned()
    {
        var decision = SkipPolicy.Classify(Rules(), Repo(), stored: null);

        Assert.False(decision.Skip);
        Assert.Equal(ScanReason.NoStoredState, decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASHAThatCouldNotBeReadMeansScanNeverSkip(string? sha)
    {
        var decision = SkipPolicy.Classify(Rules(), Repo(sha: sha), Stored());

        Assert.False(decision.Skip);
        Assert.Equal(ScanReason.UnknownKey, decision.Reason);
    }

    [Fact]
    public void AStoredScanWithNoSHAMeansScan()
    {
        var decision = SkipPolicy.Classify(Rules(), Repo(), Stored(sha: ""));

        Assert.Equal((false, ScanReason.UnknownKey), (decision.Skip, decision.Reason));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("toolVersion")]
    [InlineData("rulesVersion")]
    [InlineData("configHash")]
    public void AnUnknownPartOfTheToolsKeyMeansScanEvenIfTheStoredScanAlsoLeftItBlank(string blank)
    {
        var key = blank switch
        {
            "mode" => Key with { ScanMode = "" },
            "toolVersion" => Key with { ToolVersion = "" },
            "rulesVersion" => Key with { RulesVersion = "" },
            _ => Key with { ConfigHash = "" }
        };

        // The stored scan has the same blank part, so the two "match", but a blank is unknown, not equal.
        var decision = SkipPolicy.Classify(Rules(key: key), Repo(), Stored(key: key));

        Assert.Equal((false, ScanReason.UnknownKey), (decision.Skip, decision.Reason));
    }

    [Fact]
    public void AFullModeRunScansEverythingEvenWhenNothingChanged()
    {
        var decision = SkipPolicy.Classify(Rules(mode: ScanMode.Full), Repo(), Stored());

        Assert.Equal((false, ScanReason.FullMode), (decision.Skip, decision.Reason));
    }

    [Fact]
    public void AToolFlaggedForAFullRescanScansEverything()
    {
        var decision = SkipPolicy.Classify(Rules(forceFull: true), Repo(), Stored());

        Assert.Equal((false, ScanReason.ForceFull), (decision.Skip, decision.Reason));
    }

    [Fact]
    public void ANeverSkipToolScansEverything()
    {
        var decision = SkipPolicy.Classify(Rules(neverSkip: true), Repo(), Stored());

        Assert.Equal((false, ScanReason.NeverSkip), (decision.Skip, decision.Reason));
    }

    [Fact]
    public void AScheduledFullRescanThatIsDueScansEverything()
    {
        var decision = SkipPolicy.Classify(Rules(fullRescanDue: true), Repo(), Stored());

        Assert.Equal((false, ScanReason.FullRescanDue), (decision.Skip, decision.Reason));
    }

    [Fact]
    public void SkippingTurnedOffInEngineJsonScansEverything()
    {
        var decision = SkipPolicy.Classify(Rules(enabled: false, mode: ScanMode.Normal), Repo(), Stored());

        Assert.Equal((false, ScanReason.SkipDisabled), (decision.Skip, decision.Reason));
    }

    // ---- the scheduled full rescan ----

    [Fact]
    public void AFullRescanIsDueOnceMoreThanTheConfiguredDaysHavePassed()
    {
        Assert.False(SkipRules.IsFullRescanDue(new ToolInventoryState { LastFullScanAt = Now.AddDays(-29) }, 30, Now));
        Assert.False(SkipRules.IsFullRescanDue(new ToolInventoryState { LastFullScanAt = Now.AddDays(-30) }, 30, Now));
        Assert.True(SkipRules.IsFullRescanDue(new ToolInventoryState { LastFullScanAt = Now.AddDays(-30).AddSeconds(-1) }, 30, Now));
    }

    [Fact]
    public void AToolThatHasNeverFinishedAFullPassIsDue()
    {
        Assert.True(SkipRules.IsFullRescanDue(new ToolInventoryState(), 30, Now));
    }

    // ---- canaries ----

    private static List<RepoOutcome> Repos(int count) =>
        Enumerable.Range(1, count).Select(n => Repo($"gh-{n:D4}")).ToList();

    private static SkipPlan Plan(int count, int canaryPercent, int seed = 1, Func<RepoOutcome, bool>? skippable = null)
    {
        var repos = Repos(count);
        return SkipPlanner.Plan(
            repos,
            Rules(canaryPercent: canaryPercent),
            key => (skippable?.Invoke(repos.Single(r => r.Key == key)) ?? true) ? Stored() : null,
            new Random(seed));
    }

    [Theory]
    [InlineData(100, 5, 5)]
    [InlineData(10, 5, 1)]     // always at least one while anything is skippable
    [InlineData(1, 5, 1)]
    [InlineData(100, 0, 0)]
    [InlineData(100, 100, 100)]
    [InlineData(200, 10, 20)]
    [InlineData(0, 5, 0)]
    public void TheCanaryCountIsThePercentageOfTheSkippableReposRoundedUp(int skippable, int percent, int expected)
    {
        var plan = Plan(skippable, percent);

        Assert.Equal(expected, plan.CanaryCount);
        Assert.Equal(skippable - expected, plan.UnchangedCount);
    }

    [Fact]
    public void ACanaryIsScannedAndFlaggedAsOne()
    {
        var plan = Plan(10, 10);

        var canary = Repos(10).Select(r => (r.Key, Decision: plan.DecisionFor(r.Key))).Single(d => d.Decision.Canary);

        Assert.False(canary.Decision.Skip);
        Assert.Equal(ScanReason.Canary, canary.Decision.Reason);
        Assert.Null(plan.SkipMarkerFor(canary.Key));
    }

    [Fact]
    public void OnlyReposThatCouldHaveBeenSkippedAreEverPickedAsCanaries()
    {
        // Odd-numbered repos have no stored scan, so they are scanned for that reason and are not canary candidates.
        var plan = Plan(20, 100, skippable: r => int.Parse(r.Key[3..]) % 2 == 0);

        foreach (var repo in Repos(20))
        {
            var decision = plan.DecisionFor(repo.Key);
            var isEven = int.Parse(repo.Key[3..]) % 2 == 0;
            Assert.Equal(isEven ? ScanReason.Canary : ScanReason.NoStoredState, decision.Reason);
            Assert.Equal(isEven, decision.Canary);
        }

        Assert.Equal(10, plan.CanaryCount);
    }

    [Fact]
    public void TheSameSeedPicksTheSameCanariesAndOtherSeedsPickOthers()
    {
        var first = Repos(100).Where(r => Plan(100, 5, seed: 7).DecisionFor(r.Key).Canary).Select(r => r.Key).ToList();
        var again = Repos(100).Where(r => Plan(100, 5, seed: 7).DecisionFor(r.Key).Canary).Select(r => r.Key).ToList();
        var other = Repos(100).Where(r => Plan(100, 5, seed: 8).DecisionFor(r.Key).Canary).Select(r => r.Key).ToList();

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void EveryRepoGetsPickedAsACanaryNowAndThenNotJustTheFirstFew()
    {
        var picked = new HashSet<string>();
        for (var seed = 0; seed < 200; seed++)
        {
            var plan = Plan(40, 5, seed);
            picked.UnionWith(Repos(40).Where(r => plan.DecisionFor(r.Key).Canary).Select(r => r.Key));
        }

        Assert.Equal(40, picked.Count);
    }

    [Fact]
    public void TheMarkerForASkippedRepoCarriesTheKeyTheShaAndWhenItWasLastScanned()
    {
        var plan = Plan(3, 0);

        var marker = plan.SkipMarkerFor("gh-0002")!;

        Assert.Equal("Unchanged", marker.Reason);
        Assert.Equal("sha-1", marker.Sha);
        Assert.Equal(Key.ToMarkerKey(), marker.Key);
        Assert.Equal(Now.AddDays(-3), marker.LastScannedAt);
    }

    [Fact]
    public void AReposThePlanDidNotCoverIsScanned()
    {
        var decision = Plan(2, 0).DecisionFor("gh-9999");

        Assert.False(decision.Skip);
    }

    [Fact]
    public void ReposThatAreNotSkippableKeepTheirOwnReasonAndAreNeverCanaries()
    {
        var repos = Repos(5);
        var plan = SkipPlanner.Plan(repos, Rules(mode: ScanMode.Full, canaryPercent: 100), _ => Stored(), new Random(1));

        Assert.All(repos, r => Assert.Equal(ScanReason.FullMode, plan.DecisionFor(r.Key).Reason));
        Assert.Equal(0, plan.CanaryCount);
    }

    // ---- the tool's part of the key ----

    private EngineConfig Config(EngineSettings? settings = null, string? exclusions = null) => new(
        Path.Combine(_root, "engine.json"),
        Path.Combine(_root, "repos"),
        Path.Combine(_root, "runs"),
        Path.Combine(_root, "state"),
        Path.Combine(_root, "tmp"),
        Path.Combine(_root, "logs"),
        Settings: settings,
        ExclusionsFile: exclusions);

    private static EngineSettings With(string tool, ToolSettings settings) =>
        new() { Tools = new() { [tool] = settings } };

    [Fact]
    public void TheKeyTakesTheVersionRulesAndModeFromEngineJsonAndModeDefaultsToWorktree()
    {
        var key = ToolScanKey.For(Config(With("Semgrep", new ToolSettings { Version = "1.x", RulesVersion = "p/default@1" })), "Semgrep");

        Assert.Equal(("worktree", "1.x", "p/default@1"), (key.ScanMode, key.ToolVersion, key.RulesVersion));
        Assert.True(key.IsComplete);

        var withMode = ToolScanKey.For(Config(With("TruffleHog", new ToolSettings { Version = "3.x", RulesVersion = "d", Mode = "git" })), "TruffleHog");
        Assert.Equal("git", withMode.ScanMode);
    }

    [Fact]
    public void AToolWithNoPinnedVersionsHasAnIncompleteKeySoItIsNeverSkipped()
    {
        Assert.False(ToolScanKey.For(Config(), "Semgrep").IsComplete);
        Assert.False(ToolScanKey.For(Config(With("Semgrep", new ToolSettings { Version = "1.x" })), "Semgrep").IsComplete);
    }

    [Fact]
    public void TheConfigHashIsStableAndChangesWithWhatChangesTheResults()
    {
        string Hash(ToolSettings s, string? exclusions = null) => ToolScanKey.ComputeConfigHash(Config(exclusions: exclusions), s);
        var baseline = new ToolSettings { Mode = "filesystem", Verify = false, Packs = ["p/default", "p/owasp-top-ten"] };

        Assert.Equal(Hash(baseline), Hash(baseline with { }));
        Assert.Equal(64, Hash(baseline).Length);
        Assert.NotEqual(Hash(baseline), Hash(baseline with { Mode = "git" }));
        Assert.NotEqual(Hash(baseline), Hash(baseline with { Verify = true }));
        Assert.NotEqual(Hash(baseline), Hash(baseline with { Packs = ["p/default"] }));
        Assert.NotEqual(Hash(baseline), Hash(baseline with { Packs = ["p/default", "p/other"] }));
    }

    [Fact]
    public void TheOrderOfRulePacksDoesNotChangeTheHash()
    {
        string Hash(ToolSettings s) => ToolScanKey.ComputeConfigHash(Config(), s);

        Assert.Equal(
            Hash(new ToolSettings { Packs = ["a", "b", "c"] }),
            Hash(new ToolSettings { Packs = ["c", "a", "b"] }));
    }

    [Fact]
    public void SettingsThatOnlyChangeHowAToolRunsDoNotChangeTheHash()
    {
        string Hash(ToolSettings s) => ToolScanKey.ComputeConfigHash(Config(), s);
        var baseline = new ToolSettings { Mode = "filesystem", Packs = ["p/default"] };

        var tuned = baseline with
        {
            Enabled = false,
            MaxParallel = 2,
            TimeoutMinutes = 5,
            TokenSecret = "other-secret",
            Env = new() { ["X"] = "y" },
            LongPaths = false
        };

        Assert.Equal(Hash(baseline), Hash(tuned));
    }

    [Fact]
    public void TheSharedExclusionsFileIsPartOfTheHash()
    {
        var file = Path.Combine(_root, "exclusions.txt");
        var settings = new ToolSettings { Mode = "filesystem" };
        string Hash() => ToolScanKey.ComputeConfigHash(Config(exclusions: file), settings);

        var withoutFile = ToolScanKey.ComputeConfigHash(Config(), settings);
        File.WriteAllText(file, "node_modules\nbin\n");
        var first = Hash();
        File.WriteAllText(file, "node_modules\nbin\nobj\n");
        var changed = Hash();

        Assert.NotEqual(withoutFile, first);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void LineEndingsAndTrailingSpacesInTheExclusionsFileAreNotChanges()
    {
        var file = Path.Combine(_root, "exclusions.txt");
        var settings = new ToolSettings { Mode = "filesystem" };
        string HashOf(string content)
        {
            File.WriteAllText(file, content);
            return ToolScanKey.ComputeConfigHash(Config(exclusions: file), settings);
        }

        var unix = HashOf("node_modules\nbin\n");

        Assert.Equal(unix, HashOf("node_modules\r\nbin\r\n"));
        Assert.Equal(unix, HashOf("node_modules  \nbin\t\n\n\n"));
    }

    [Theory]
    [InlineData("Snyk", "dependencies", null, true)]
    [InlineData("Snyk", "code", null, false)]
    [InlineData("Snyk", null, null, false)]
    [InlineData("TruffleHog", "filesystem", true, true)]
    [InlineData("TruffleHog", "filesystem", false, false)]
    [InlineData("TruffleHog", null, null, false)]
    [InlineData("Semgrep", "dependencies", true, false)]
    public void OnlySnykInDependencyModeAndTruffleHogWithVerificationAreNeverSkipped(string tool, string? mode, bool? verify, bool expected)
    {
        Assert.Equal(expected, ToolScanKey.NeverSkip(tool, new ToolSettings { Mode = mode, Verify = verify }));
    }

    [Fact]
    public void KeyMatchingComparesEveryPartExactly()
    {
        Assert.True(Key.Matches(Stored(), "sha-1"));
        Assert.False(Key.Matches(Stored(), "SHA-1"));
        Assert.False(Key.Matches(Stored() with { ConfigHash = "Hash-1" }, "sha-1"));
    }

    // ---- exclusions in engine.json ----

    [Fact]
    public async Task TheExclusionsFileInEngineJsonIsResolvedAndMustExist()
    {
        var configFile = Path.Combine(_root, "engine.json");
        var exclusions = Path.Combine(_root, "shared", "exclusions.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(exclusions)!);
        await File.WriteAllTextAsync(exclusions, "bin\n");

        string Json(string? path) => "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\""
            + (path is null ? "" : $", \"exclusions\": \"{path}\"") + " } }";

        await File.WriteAllTextAsync(configFile, Json("shared/exclusions.txt"));
        Assert.Equal(exclusions, (await EngineConfig.LoadAsync(configFile, CancellationToken.None)).ExclusionsFile);

        await File.WriteAllTextAsync(configFile, Json(null));
        Assert.Null((await EngineConfig.LoadAsync(configFile, CancellationToken.None)).ExclusionsFile);

        await File.WriteAllTextAsync(configFile, Json("shared/missing.txt"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EngineConfig.LoadAsync(configFile, CancellationToken.None));
        Assert.Contains("paths.exclusions", error.Message);
    }
}
