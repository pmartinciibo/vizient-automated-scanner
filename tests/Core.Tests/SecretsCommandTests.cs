namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Tools;

public class SecretsCommandTests : IDisposable
{
    private const string Value = "tok-VALUE-90210";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T03:00:00Z");
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public SecretsCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeConsole(string account = "DOM\\svc-scanner") : ISecretsConsole
    {
        public StringWriter OutWriter { get; } = new();
        public StringWriter ErrorWriter { get; } = new();
        public TextWriter Out => OutWriter;
        public TextWriter Error => ErrorWriter;
        public string CurrentAccount => account;
        public string? Typed { get; set; }
        public string? Piped { get; set; }
        public string? Prompt { get; private set; }
        public string All => OutWriter.ToString() + ErrorWriter;

        public string? ReadSecretWithoutEcho(string prompt)
        {
            Prompt = prompt;
            return Typed;
        }

        public string? ReadStdinLine() => Piped;
    }

    private EngineConfig Config(EngineSettings? settings = null) => new(
        Path.Combine(_root, "engine.json"), Path.Combine(_root, "repos"), Path.Combine(_root, "runs"),
        Path.Combine(_root, "state"), Path.Combine(_root, "tmp"), Path.Combine(_root, "logs"),
        Settings: settings);

    private string SecretsPath => Path.Combine(_root, "secrets.dat");

    private Task<int> RunAsync(FakeConsole io, EngineSettings? settings, params string[] args) =>
        SecretsCommand.RunAsync(args, Config(settings), io, new FixedTime(Now));

    private static EngineSettings RequiringAccount(string account) =>
        new() { Secrets = new SecretsSettings { RequiredAccount = account } };

    private async Task<SecretStore> StoreAsync() => await SecretStore.LoadAsync(SecretsPath);

    // ---- set ----

    [Fact]
    public async Task SetAsksForTheValueWithoutShowingItAndSavesItEncrypted()
    {
        var io = new FakeConsole { Typed = Value };

        var exit = await RunAsync(io, null, "set", "ado-pat");

        Assert.Equal(0, exit);
        Assert.Equal("Value for secret 'ado-pat': ", io.Prompt);
        Assert.Equal(Value, (await StoreAsync()).GetRequired("ado-pat", Today, "test"));
        Assert.DoesNotContain(Value, io.All);
        Assert.Contains("Saved secret 'ado-pat', does not expire.", io.All);
        Assert.DoesNotContain(Value, await File.ReadAllTextAsync(SecretsPath, System.Text.Encoding.Latin1));
    }

    [Fact]
    public async Task SetCanTakeTheValueFromStandardInput()
    {
        var io = new FakeConsole { Piped = Value };

        var exit = await RunAsync(io, null, "set", "ado-pat", "--stdin");

        Assert.Equal(0, exit);
        Assert.Null(io.Prompt);
        Assert.Equal(Value, (await StoreAsync()).GetRequired("ado-pat", Today, "test"));
    }

    [Fact]
    public async Task SetRecordsTheExpiryDate()
    {
        var io = new FakeConsole { Piped = Value };

        await RunAsync(io, null, "set", "ado-pat", "--stdin", "--expires", "2027-01-10");

        Assert.Equal(new DateOnly(2027, 1, 10), (await StoreAsync()).Info("ado-pat", Today, 30)!.ExpiresAt);
        Assert.Contains("expires 2027-01-10", io.All);
    }

    [Fact]
    public async Task SettingAnExistingSecretReplacesItAndSaysSo()
    {
        await RunAsync(new FakeConsole { Piped = "old-VALUE" }, null, "set", "ado-pat", "--stdin");
        var io = new FakeConsole { Piped = "new-VALUE" };

        await RunAsync(io, null, "set", "ado-pat", "--stdin");

        Assert.Contains("Replaced secret 'ado-pat'", io.All);
        Assert.Equal("new-VALUE", (await StoreAsync()).GetRequired("ado-pat", Today, "test"));
    }

    [Fact]
    public async Task SpacesAroundAPastedValueAreNotStored()
    {
        await RunAsync(new FakeConsole { Piped = "  ghp_VALUE-123 	" }, null, "set", "github-token", "--stdin");

        Assert.Equal("ghp_VALUE-123", (await StoreAsync()).GetRequired("github-token", Today, "test"));
    }

    [Fact]
    public async Task SettingOneSecretLeavesTheOthersAlone()
    {
        await RunAsync(new FakeConsole { Piped = "first-VALUE" }, null, "set", "a", "--stdin");
        await RunAsync(new FakeConsole { Piped = "second-VALUE" }, null, "set", "b", "--stdin");

        var store = await StoreAsync();

        Assert.Equal("first-VALUE", store.GetRequired("a", Today, "t"));
        Assert.Equal("second-VALUE", store.GetRequired("b", Today, "t"));
    }

    [Fact]
    public async Task ThereIsNoValueOption()
    {
        var io = new FakeConsole();

        var exit = await RunAsync(io, null, "set", "ado-pat", "--value", Value);

        Assert.Equal(2, exit);
        Assert.False(File.Exists(SecretsPath));
        Assert.Contains("Usage:", io.All);
        // The command line value must not be echoed back either.
        Assert.DoesNotContain(Value, io.All);
    }

    [Theory]
    [InlineData(null, "there is no interactive console")]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    public async Task SetWithNoUsableValueFailsAndWritesNothing(string? typed, string expected)
    {
        var io = new FakeConsole { Typed = typed };

        var exit = await RunAsync(io, null, "set", "ado-pat");

        Assert.Equal(2, exit);
        Assert.Contains(expected, io.All);
        Assert.False(File.Exists(SecretsPath));
    }

    [Fact]
    public async Task SetWithNothingOnStandardInputFails()
    {
        var io = new FakeConsole { Piped = null };

        Assert.Equal(2, await RunAsync(io, null, "set", "ado-pat", "--stdin"));
        Assert.Contains("nothing was provided", io.All);
    }

    [Theory]
    [InlineData("--expires", "01/10/2027")]
    [InlineData("--expires", "tomorrow")]
    [InlineData("--expires", "2026-10-13")] // yesterday
    public async Task ABadOrPastExpiryDateIsRejected(string option, string date)
    {
        var io = new FakeConsole { Piped = Value };

        var exit = await RunAsync(io, null, "set", "ado-pat", "--stdin", option, date);

        Assert.Equal(2, exit);
        Assert.False(File.Exists(SecretsPath));
    }

    [Fact]
    public async Task ABadSecretNameIsRejected()
    {
        var io = new FakeConsole { Piped = Value };

        Assert.Equal(2, await RunAsync(io, null, "set", "not a name", "--stdin"));
        Assert.False(File.Exists(SecretsPath));
        Assert.DoesNotContain(Value, io.All);
    }

    [Fact]
    public async Task TheFileOptionWorksOnThatFileInsteadOfTheOneBesideTheEngine()
    {
        var other = Path.Combine(_root, "elsewhere", "other.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        var io = new FakeConsole { Piped = Value };

        var exit = await RunAsync(io, null, "set", "ado-pat", "--stdin", "--file", other);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(other));
        Assert.False(File.Exists(SecretsPath));
        Assert.Contains(other, io.All);
    }

    [Fact]
    public async Task TheCommandSaysWhichFileItIsWorkingOn()
    {
        var io = new FakeConsole { Piped = Value };

        await RunAsync(io, null, "set", "ado-pat", "--stdin");

        Assert.Contains($"Secrets file: {SecretsPath}", io.All);
    }

    // ---- the required account ----

    [Fact]
    public async Task SetAndRemoveRefuseToRunAsAnyOtherAccountThanTheRequiredOne()
    {
        var settings = RequiringAccount("DOM\\svc-scanner");
        await RunAsync(new FakeConsole("DOM\\svc-scanner") { Piped = Value }, settings, "set", "ado-pat", "--stdin");

        var setIo = new FakeConsole("DOM\\someone-else") { Piped = Value };
        var removeIo = new FakeConsole("DOM\\someone-else");

        Assert.Equal(2, await RunAsync(setIo, settings, "set", "other", "--stdin"));
        Assert.Equal(2, await RunAsync(removeIo, settings, "remove", "ado-pat"));
        Assert.Contains("must be run as DOM\\svc-scanner", setIo.All);
        Assert.Contains("DOM\\someone-else", removeIo.All);
        var store = await StoreAsync();
        Assert.True(store.Contains("ado-pat"));
        Assert.False(store.Contains("other"));
    }

    [Fact]
    public async Task TheRequiredAccountIsMatchedWithoutRegardToCase()
    {
        var io = new FakeConsole("dom\\SVC-SCANNER") { Piped = Value };

        Assert.Equal(0, await RunAsync(io, RequiringAccount("DOM\\svc-scanner"), "set", "ado-pat", "--stdin"));
    }

    [Fact]
    public async Task ListAndCheckAreAllowedFromAnyAccount()
    {
        await RunAsync(new FakeConsole("DOM\\svc-scanner") { Piped = Value }, null, "set", "ado-pat", "--stdin");
        var settings = RequiringAccount("DOM\\svc-scanner");

        Assert.Equal(0, await RunAsync(new FakeConsole("DOM\\someone-else"), settings, "list"));
        Assert.Equal(0, await RunAsync(new FakeConsole("DOM\\someone-else"), settings, "check"));
    }

    // ---- remove ----

    [Fact]
    public async Task RemoveDeletesOnlyThatSecret()
    {
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "a", "--stdin");
        await RunAsync(new FakeConsole { Piped = "b-VALUE" }, null, "set", "b", "--stdin");
        var io = new FakeConsole();

        var exit = await RunAsync(io, null, "remove", "a");

        Assert.Equal(0, exit);
        Assert.Contains("Removed secret 'a'", io.All);
        var store = await StoreAsync();
        Assert.False(store.Contains("a"));
        Assert.True(store.Contains("b"));
    }

    [Fact]
    public async Task RemovingASecretThatIsNotThereIsAnError()
    {
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "a", "--stdin");
        var io = new FakeConsole();

        Assert.Equal(2, await RunAsync(io, null, "remove", "missing"));
        Assert.Contains("no secret named 'missing'", io.All);
    }

    // ---- list ----

    [Fact]
    public async Task ListShowsNamesCreatedExpiryAndStatusButNeverValues()
    {
        await RunAsync(new FakeConsole { Piped = "zzz-VALUE-1" }, null, "set", "no-expiry", "--stdin");
        await RunAsync(new FakeConsole { Piped = "aaa-VALUE-2" }, null, "set", "soon", "--stdin", "--expires", "2026-10-24");
        var io = new FakeConsole();

        var exit = await RunAsync(io, null, "list");

        Assert.Equal(0, exit);
        Assert.Contains("NAME", io.All);
        Assert.Contains("no-expiry", io.All);
        Assert.Contains("never", io.All);
        Assert.Contains("2026-10-24", io.All);
        Assert.Contains("expiring in 10 days", io.All);
        Assert.Contains("2026-10-14T03:00:00Z", io.All);
        Assert.DoesNotContain("VALUE", io.All);
    }

    [Fact]
    public async Task ListWithNoFileSaysSoAndIsNotAnError()
    {
        var io = new FakeConsole();

        Assert.Equal(0, await RunAsync(io, null, "list"));
        Assert.Contains("does not exist yet", io.All);
    }

    // ---- check ----

    private static EngineSettings Needing(params string[] secrets) => new()
    {
        Secrets = new SecretsSettings { ExpiryWarnDays = 30 },
        Tools = new()
        {
            ["Rapid7"] = new ToolSettings { TokenSecret = secrets.ElementAtOrDefault(0) },
            ["Snyk"] = new ToolSettings { Env = secrets.Length > 1 ? new() { ["SNYK_TOKEN"] = secrets[1] } : null }
        }
    };

    [Fact]
    public async Task CheckPassesWhenEverySecretEngineJsonNamesIsThereAndInDate()
    {
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "rapid7-token", "--stdin");
        await RunAsync(new FakeConsole { Piped = "b-VALUE" }, null, "set", "snyk-token", "--stdin");
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token", "snyk-token"), "check");

        Assert.Equal(0, exit);
        Assert.Contains("OK        rapid7-token", io.All);
        Assert.Contains("2 ok, 0 warnings, 0 errors.", io.All);
        Assert.DoesNotContain("VALUE", io.All);
    }

    [Fact]
    public async Task CheckFailsWithExitCode2WhenASecretIsMissing()
    {
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "rapid7-token", "--stdin");
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token", "snyk-token"), "check");

        Assert.Equal(2, exit);
        Assert.Contains("MISSING   snyk-token: has not been set. Needed by Snyk (SNYK_TOKEN).", io.All);
        Assert.Contains("1 ok, 0 warnings, 1 errors.", io.All);
    }

    [Fact]
    public async Task CheckWarnsWithExitCode1WhenOnlyAnExpiryIsNear()
    {
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "rapid7-token", "--stdin", "--expires", "2026-11-01");
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token"), "check");

        Assert.Equal(1, exit);
        Assert.Contains("EXPIRING  rapid7-token: expires 2026-11-01 (18 days). Needed by Rapid7.", io.All);
    }

    [Fact]
    public async Task CheckFailsWhenASecretHasExpired()
    {
        var path = SecretsPath;
        var store = new SecretStore();
        store.Set("rapid7-token", "a-VALUE", null, Now);
        await store.SaveAsync(path);
        var expired = new SecretsDocument
        {
            Secrets = new() { ["rapid7-token"] = new SecretEntry { Value = "a-VALUE", CreatedAt = Now.AddDays(-90), ExpiresAt = Today.AddDays(-2) } }
        };
        await SecretsFile.WriteAsync(path, expired);
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token"), "check");

        Assert.Equal(2, exit);
        Assert.Contains("EXPIRED", io.All);
        Assert.Contains("expired on 2026-10-12", io.All);
    }

    [Fact]
    public async Task CheckWithNoFileReportsEverySecretMissing()
    {
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token", "snyk-token"), "check");

        Assert.Equal(2, exit);
        Assert.Contains("does not exist yet", io.All);
        Assert.Contains("0 ok, 0 warnings, 2 errors.", io.All);
    }

    [Fact]
    public async Task CheckOnAFileThatCannotBeDecryptedFailsWithAClearMessage()
    {
        await File.WriteAllTextAsync(SecretsPath, "not a secrets file");
        var io = new FakeConsole();

        var exit = await RunAsync(io, Needing("rapid7-token"), "check");

        Assert.Equal(2, exit);
        Assert.Contains("not a secrets file", io.All);
    }

    [Fact]
    public async Task CheckLeavesOutSecretsOfToolsThatAreTurnedOff()
    {
        var settings = Needing("rapid7-token", "snyk-token");
        settings.Tools["Snyk"] = settings.Tools["Snyk"] with { Enabled = false };
        await RunAsync(new FakeConsole { Piped = "a-VALUE" }, null, "set", "rapid7-token", "--stdin");

        Assert.Equal(0, await RunAsync(new FakeConsole(), settings, "check"));
    }

    // ---- reading the command line ----

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("set")]                                  // no name
    [InlineData("remove")]
    [InlineData("list", "extra")]                        // takes no name
    [InlineData("check", "extra")]
    [InlineData("set", "a", "b")]                        // two names
    [InlineData("list", "--stdin")]                      // belongs to set
    [InlineData("check", "--expires", "2027-01-10")]
    [InlineData("set", "a", "--expires")]                // no date given
    [InlineData("list", "--file")]                       // no path given
    [InlineData("set", "a", "--nonsense")]
    public async Task ABadCommandLineGivesTheUsageAndExitCode2(params string[] args)
    {
        var io = new FakeConsole { Piped = Value };

        var exit = await RunAsync(io, null, args);

        Assert.Equal(2, exit);
        Assert.Contains("Usage:", io.ErrorWriter.ToString());
        Assert.False(File.Exists(SecretsPath));
    }
}
