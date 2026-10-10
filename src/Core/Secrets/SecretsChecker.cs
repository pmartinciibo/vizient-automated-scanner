using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Secrets;

public enum SecretIssue
{
    Missing,
    Expired,
    ExpiringSoon
}

/// <summary>A problem with a secret engine.json names, and what needs it.</summary>
public sealed record SecretFinding(string Name, SecretIssue Issue, DateOnly? ExpiresAt, int? DaysLeft, string RequiredBy);

/// <param name="FileError">Why the secrets file could not be read, or null. A file that is not there is not an error here: every secret is just missing.</param>
public sealed record SecretsCheckResult(
    IReadOnlyList<string> Ok,
    IReadOnlyList<SecretFinding> Findings,
    string? FileError)
{
    /// <summary>0 all good, 1 only warnings (a secret expires soon), 2 an error (unreadable file, a secret missing or expired).</summary>
    public int ExitCode =>
        FileError is not null || Findings.Any(f => f.Issue is SecretIssue.Missing or SecretIssue.Expired) ? 2
        : Findings.Count > 0 ? 1
        : 0;
}

/// <summary>Works out which secrets engine.json needs, and whether the secrets file has them in good order.</summary>
public static class SecretsChecker
{
    /// <summary>
    /// Every secret engine.json names, with what needs it: the tokens of the sources (if RepoClone is on), and for each
    /// tool that is on its token, its environment variables and the password it runs under.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RequiredSecrets(EngineSettings settings, Func<string, bool> isToolEnabled)
    {
        var required = new Dictionary<string, string>(StringComparer.Ordinal);

        void Need(string? secret, string neededBy)
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                required.TryAdd(secret, neededBy);
            }
        }

        if (isToolEnabled(ToolNames.RepoClone))
        {
            foreach (var source in settings.Sources)
            {
                Need(source.TokenSecret, $"{source.Source} source {source.Org}");
            }
        }

        foreach (var name in ToolNames.All.Where(isToolEnabled))
        {
            var tool = settings.Tool(name);
            Need(tool.TokenSecret, name);
            foreach (var (variable, secret) in tool.Env ?? [])
            {
                Need(secret, $"{name} ({variable})");
            }

            Need(tool.RunAs?.PasswordSecret, $"{name} (runAs password)");
        }

        return required;
    }

    public static SecretsCheckResult Check(
        EngineSettings settings,
        SecretStore store,
        Func<string, bool> isToolEnabled,
        DateOnly today,
        string? fileError = null)
    {
        var ok = new List<string>();
        var findings = new List<SecretFinding>();

        foreach (var (name, neededBy) in RequiredSecrets(settings, isToolEnabled).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var info = store.Info(name, today, settings.Secrets.ExpiryWarnDays);
            switch (info?.Status)
            {
                case null:
                    findings.Add(new SecretFinding(name, SecretIssue.Missing, null, null, neededBy));
                    break;
                case SecretStatus.Expired:
                    findings.Add(new SecretFinding(name, SecretIssue.Expired, info.ExpiresAt, info.DaysLeft, neededBy));
                    break;
                case SecretStatus.ExpiringSoon:
                    findings.Add(new SecretFinding(name, SecretIssue.ExpiringSoon, info.ExpiresAt, info.DaysLeft, neededBy));
                    break;
                default:
                    ok.Add(name);
                    break;
            }
        }

        return new SecretsCheckResult(ok, findings, fileError);
    }
}
