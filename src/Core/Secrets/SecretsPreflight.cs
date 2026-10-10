using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Secrets;

/// <summary>
/// The check the engine makes at the start of a run: the secrets file reads, and every secret the tools that are on
/// need is there and has not expired. Problems are put in the status as alerts, by name and never by value.
/// </summary>
public static class SecretsPreflight
{
    public static async Task<SecretsCheckResult> RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        SecretStore store;
        string? fileError = null;
        try
        {
            store = await context.Secrets.StoreAsync(cancellationToken);
        }
        catch (SecretsFileException ex)
        {
            store = new SecretStore();
            fileError = ex.Message;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = SecretsChecker.Check(context.Config.Current, store, context.IsEnabled, today, fileError);
        var now = DateTimeOffset.UtcNow;

        if (fileError is not null)
        {
            context.Status.AddAlert(new StatusAlert { Type = "SecretsUnreadable", Name = EngineConfig.SecretsFileName, At = now });
        }

        foreach (var finding in result.Findings)
        {
            context.Status.AddAlert(new StatusAlert
            {
                Type = finding.Issue switch
                {
                    SecretIssue.Missing => "MissingSecret",
                    SecretIssue.Expired => "ExpiredSecret",
                    _ => "ExpiringSecret"
                },
                Name = finding.Name,
                ExpiresAt = finding.ExpiresAt?.ToString("yyyy-MM-dd"),
                At = now
            });
        }

        return result;
    }
}
