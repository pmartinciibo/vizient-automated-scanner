namespace Vizient.AutomatedScanner.Tests;

using System.Security.Cryptography;
using System.Text;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;

// The encrypted file and the in-memory store. These use real Windows DPAPI, as the engine does.
public class SecretsFileTests : IDisposable
{
    private const string AdoValue = "ado-pat-VALUE-8472913";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T03:00:00Z");
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _file;

    public SecretsFileTests()
    {
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "secrets.dat");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SecretsDocument Document() => new()
    {
        Secrets = new(StringComparer.Ordinal)
        {
            ["ado-pat"] = new SecretEntry { Value = AdoValue, CreatedAt = Now, ExpiresAt = new DateOnly(2027, 1, 10) },
            ["github-token"] = new SecretEntry { Value = "gh-VALUE-5550001", CreatedAt = Now, ExpiresAt = null }
        }
    };

    // ---- the file ----

    [Fact]
    public async Task ASecretsFileRoundTripsWithItsValuesAndDates()
    {
        await SecretsFile.WriteAsync(_file, Document());

        var read = await SecretsFile.ReadAsync(_file);

        Assert.Equal(1, read.SchemaVersion);
        Assert.Equal(["ado-pat", "github-token"], read.Secrets.Keys.Order());
        Assert.Equal(AdoValue, read.Secrets["ado-pat"].Value);
        Assert.Equal(new DateOnly(2027, 1, 10), read.Secrets["ado-pat"].ExpiresAt);
        Assert.Equal(Now, read.Secrets["ado-pat"].CreatedAt);
        Assert.Null(read.Secrets["github-token"].ExpiresAt);
    }

    [Fact]
    public async Task TheFileStartsWithTheClearHeaderThenTheEncryptedBlob()
    {
        await SecretsFile.WriteAsync(_file, Document());

        var bytes = await File.ReadAllBytesAsync(_file);

        Assert.Equal("VSCRT001", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public async Task NeitherTheValuesNorTheNamesAreReadableInTheFile()
    {
        await SecretsFile.WriteAsync(_file, Document());
        var bytes = await File.ReadAllBytesAsync(_file);

        foreach (var text in new[] { AdoValue, "gh-VALUE-5550001", "ado-pat", "github-token", "secrets", "expiresAt", "2027-01-10" })
        {
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0, $"'{text}' is readable in the file");
            Assert.False(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0, $"'{text}' is readable in the file as UTF-16");
        }
    }

    [Fact]
    public async Task ARewriteReplacesTheFileAtomicallyAndLeavesNoTemporaryFile()
    {
        await SecretsFile.WriteAsync(_file, Document());
        await SecretsFile.WriteAsync(_file, new SecretsDocument());

        Assert.Empty((await SecretsFile.ReadAsync(_file)).Secrets);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task EachWriteKeepsThePreviousVersionAsAProtectedBackup()
    {
        await SecretsFile.WriteAsync(_file, Document());
        var first = await File.ReadAllBytesAsync(_file);

        await SecretsFile.WriteAsync(_file, new SecretsDocument());

        Assert.Equal(first, await File.ReadAllBytesAsync(_file + ".bak"));
        Assert.Equal(2, (await SecretsFile.ReadAsync(_file + ".bak")).Secrets.Count);
    }

    [Fact]
    public async Task AFileThatIsNotASecretsFileIsNeverCopiedOverTheBackup()
    {
        await SecretsFile.WriteAsync(_file, Document());
        await SecretsFile.WriteAsync(_file, Document());
        var goodBackup = await File.ReadAllBytesAsync(_file + ".bak");
        await File.WriteAllTextAsync(_file, "this is not a secrets file");

        await SecretsFile.WriteAsync(_file, Document());

        Assert.Equal(goodBackup, await File.ReadAllBytesAsync(_file + ".bak"));
    }

    // ---- files that cannot be read ----

    [Fact]
    public async Task AFileWithoutTheHeaderIsNotASecretsFile()
    {
        await File.WriteAllTextAsync(_file, "{ \"secrets\": { \"ado-pat\": \"" + AdoValue + "\" } }");

        var error = await Assert.ThrowsAsync<SecretsFileException>(() => SecretsFile.ReadAsync(_file));

        Assert.Contains("not a secrets file", error.Message);
        Assert.DoesNotContain(AdoValue, error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task ATruncatedFileIsNotASecretsFile(int length)
    {
        await File.WriteAllBytesAsync(_file, Encoding.ASCII.GetBytes("VSCRT001").Take(length).ToArray());

        await Assert.ThrowsAsync<SecretsFileException>(() => SecretsFile.ReadAsync(_file));
    }

    [Fact]
    public async Task AHeaderWithJunkAfterItCannotBeDecrypted()
    {
        await File.WriteAllBytesAsync(_file, [.. Encoding.ASCII.GetBytes("VSCRT001"), .. new byte[200]]);

        var error = await Assert.ThrowsAsync<SecretsFileException>(() => SecretsFile.ReadAsync(_file));

        Assert.Contains("cannot be decrypted", error.Message);
    }

    [Fact]
    public async Task ADamagedBlobCannotBeDecrypted()
    {
        await SecretsFile.WriteAsync(_file, Document());
        var bytes = await File.ReadAllBytesAsync(_file);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(_file, bytes);

        await Assert.ThrowsAsync<SecretsFileException>(() => SecretsFile.ReadAsync(_file));
    }

    [Fact]
    public async Task ABlobProtectedWithoutTheApplicationsEntropyCannotBeDecrypted()
    {
        // The same user's data, protected by some other program using DPAPI with different entropy.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var plain = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"secrets\":{}}");
        var blob = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes("someone else"), DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_file, [.. Encoding.ASCII.GetBytes("VSCRT001"), .. blob]);

        await Assert.ThrowsAsync<SecretsFileException>(() => SecretsFile.ReadAsync(_file));
    }

    // ---- values never show ----

    [Fact]
    public void ASecretEntryNeverPrintsItsValue()
    {
        var entry = new SecretEntry { Value = AdoValue, CreatedAt = Now, ExpiresAt = new DateOnly(2027, 1, 10) };

        Assert.DoesNotContain(AdoValue, entry.ToString());
        Assert.Contains("***", entry.ToString());
        Assert.DoesNotContain(AdoValue, Document().ToString());
        Assert.DoesNotContain(AdoValue, $"{Document().Secrets["ado-pat"]}");
    }

    // ---- the store in memory ----

    [Fact]
    public void ASecretIsSetAndReadBack()
    {
        var store = new SecretStore();
        store.Set("rapid7-token", "r7-VALUE", null, Now);

        Assert.True(store.Contains("rapid7-token"));
        Assert.Equal("r7-VALUE", store.GetRequired("rapid7-token", Today, "Rapid7"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void SettingAnExistingSecretReplacesItsValueAndRestartsItsClock()
    {
        var store = new SecretStore();
        store.Set("ado-pat", "old", new DateOnly(2026, 11, 1), Now);

        store.Set("ado-pat", "new", null, Now.AddDays(5));

        Assert.Equal("new", store.GetRequired("ado-pat", Today, "x"));
        var info = store.Info("ado-pat", Today, 30)!;
        Assert.Equal((Now.AddDays(5), (DateOnly?)null), (info.CreatedAt, info.ExpiresAt));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ARemovedSecretIsGone()
    {
        var store = new SecretStore();
        store.Set("ado-pat", "v", null, Now);

        Assert.True(store.Remove("ado-pat"));
        Assert.False(store.Remove("ado-pat"));
        Assert.False(store.Contains("ado-pat"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("-leading-dash")]
    public void ANameThatIsNotPlainIsRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new SecretStore().Set(name, "v", null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyValueIsRejected(string? value)
    {
        Assert.Throws<ArgumentException>(() => new SecretStore().Set("ado-pat", value!, null, Now));
    }

    [Fact]
    public void AnExpiryDateThatHasAlreadyPassedIsRejectedButTodayIsFine()
    {
        var store = new SecretStore();

        Assert.Throws<ArgumentException>(() => store.Set("ado-pat", "v", Today.AddDays(-1), Now));
        store.Set("ado-pat", "v", Today, Now);
    }

    [Fact]
    public void AMissingSecretIsReportedByNameWithAHintAndNoValue()
    {
        var error = Assert.Throws<SecretUnavailableException>(() => new SecretStore().GetRequired("snyk-token", Today, "Snyk (SNYK_TOKEN)"));

        Assert.Contains("'snyk-token'", error.Message);
        Assert.Contains("Snyk (SNYK_TOKEN)", error.Message);
        Assert.Contains("engine secrets set snyk-token", error.Message);
    }

    [Fact]
    public void AnExpiredSecretIsRefusedAndTheLastGoodDayIsStillGood()
    {
        var store = new SecretStore();
        store.Set("ado-pat", "ado-VALUE-9", Today.AddDays(2), Now);

        Assert.Equal("ado-VALUE-9", store.GetRequired("ado-pat", Today.AddDays(2), "x"));
        var error = Assert.Throws<SecretUnavailableException>(() => store.GetRequired("ado-pat", Today.AddDays(3), "x"));
        Assert.Contains("expired on", error.Message);
        Assert.DoesNotContain("ado-VALUE-9", error.Message);
    }

    [Fact]
    public void ListingShowsNamesDatesAndStatusButNeverValues()
    {
        var store = new SecretStore();
        store.Set("zeta", "z-VALUE", null, Now);
        store.Set("alpha", "a-VALUE", Today.AddDays(10), Now);
        store.Set("old", "o-VALUE", Today, Now);

        var list = store.List(Today.AddDays(1), warnDays: 30);

        Assert.Equal(["alpha", "old", "zeta"], list.Select(s => s.Name));
        Assert.Equal([SecretStatus.ExpiringSoon, SecretStatus.Expired, SecretStatus.Ok], list.Select(s => s.Status));
        Assert.Equal([9, -1, null], list.Select(s => s.DaysLeft));
        Assert.DoesNotContain("VALUE", string.Join(" ", list.Select(s => s.ToString())));
    }

    [Theory]
    [InlineData(30, 30, SecretStatus.ExpiringSoon)]   // exactly the warning period away
    [InlineData(31, 30, SecretStatus.Ok)]
    [InlineData(0, 30, SecretStatus.ExpiringSoon)]    // expires today, still good
    [InlineData(-1, 30, SecretStatus.Expired)]
    [InlineData(5, 0, SecretStatus.Ok)]               // a warning period of 0 warns only on the last day
    [InlineData(0, 0, SecretStatus.ExpiringSoon)]
    public async Task TheWarningPeriodIsInclusive(int daysFromToday, int warnDays, SecretStatus expected)
    {
        // Written as a file so the expiry can be one that has already passed, which Set would refuse.
        var path = Path.Combine(_root, "status.dat");
        await SecretsFile.WriteAsync(path, new SecretsDocument
        {
            Secrets = new() { ["s"] = new SecretEntry { Value = "v", CreatedAt = Now, ExpiresAt = Today.AddDays(daysFromToday) } }
        });
        var loaded = await SecretStore.LoadAsync(path);

        Assert.Equal(expected, loaded.Info("s", Today, warnDays)!.Status);
    }

    [Fact]
    public async Task AStoreSavedToAFileLoadsBackIdentically()
    {
        var store = new SecretStore();
        store.Set("ado-pat", AdoValue, new DateOnly(2027, 1, 10), Now);
        await store.SaveAsync(_file);

        var loaded = await SecretStore.LoadAsync(_file);

        Assert.Equal(AdoValue, loaded.GetRequired("ado-pat", Today, "x"));
        Assert.Equal(new DateOnly(2027, 1, 10), loaded.Info("ado-pat", Today, 30)!.ExpiresAt);
    }

    [Fact]
    public async Task LoadingWhenThereIsNoFileGivesAnEmptyStore()
    {
        var store = await SecretStore.LoadAsync(Path.Combine(_root, "nothing-here.dat"));

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task LoadingAFileThatCannotBeReadThrowsRatherThanLookingEmpty()
    {
        await File.WriteAllTextAsync(_file, "garbage");

        await Assert.ThrowsAsync<SecretsFileException>(() => SecretStore.LoadAsync(_file));
    }
}
