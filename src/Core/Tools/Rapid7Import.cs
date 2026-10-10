using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Rapid7;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 1: imports Rapid7 InsightAppSec data into one file (rapid7.json in the Rapid7 results folder): a list of applications,
/// each with its vulnerabilities. See <see cref="Rapid7Importer"/>.
/// </summary>
/// <param name="handler">Replaces the network, for tests. By default the machine's own settings (proxy, certificates) apply.</param>
/// <param name="delay">Replaces the wait between retries, for tests.</param>
public sealed class Rapid7Import(
    HttpMessageHandler? handler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : ITool
{
    public string Name => ToolNames.Rapid7;

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var settings = context.Config.Current.Tool(Name);
        var options = Rapid7Options.From(settings);
        if (string.IsNullOrWhiteSpace(settings.TokenSecret))
        {
            throw new InvalidOperationException("The Rapid7 import needs tools.Rapid7.tokenSecret in engine.json (the name of the secret that holds the token).");
        }

        if (IsAlreadyComplete(context))
        {
            context.Status.UpdateStep(Name, step => step with { Message = "already imported in this run" });
            return;
        }

        // A missing or expired token fails the step here, before anything is requested, naming the secret.
        var token = await context.Secrets.GetSecretAsync(settings.TokenSecret, Name, cancellationToken);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own timeout, set in the client

        var client = new Rapid7Client(http, options, token, delay)
        {
            OnRetry = message => context.Status.UpdateStep(Name, step => step with { Message = message })
        };

        await new Rapid7Importer(context, client, options).RunAsync(cancellationToken);
    }

    // Resume and RetryFailed continue an earlier attempt. If its import finished (the marker is written last, after the
    // file is in place) there is nothing to redo. Otherwise the whole import runs again.
    private bool IsAlreadyComplete(RunContext context) =>
        context.Request.Mode is ScanMode.Resume or ScanMode.RetryFailed
        && File.Exists(context.Paths.ImportDoneMarker(Name))
        && File.Exists(context.Paths.ResultFile(Name, Rapid7Importer.OutputKey));
}
