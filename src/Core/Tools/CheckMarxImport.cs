using Vizient.AutomatedScanner.Core.CheckMarx;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 2: imports Checkmarx One data into one file (checkmarx.json in the CheckMarx results folder): a list of
/// applications, each with its projects and the findings of those projects. See <see cref="CheckMarxImporter"/>.
/// </summary>
/// <param name="handler">Replaces the network, for tests. By default the machine's own settings (proxy, certificates) apply.</param>
/// <param name="delay">Replaces the wait between retries, for tests.</param>
/// <param name="time">Replaces the clock the access token's age is judged by, for tests.</param>
public sealed class CheckMarxImport(
    HttpMessageHandler? handler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? time = null) : ITool
{
    public string Name => ToolNames.CheckMarx;

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var settings = context.Config.Current.Tool(Name);
        var options = CheckMarxOptions.From(settings);
        if (string.IsNullOrWhiteSpace(settings.TokenSecret))
        {
            throw new InvalidOperationException("The CheckMarx import needs tools.CheckMarx.tokenSecret in engine.json (the name of the secret that holds the refresh token).");
        }

        if (IsAlreadyComplete(context))
        {
            context.Status.UpdateStep(Name, step => step with { Message = "already imported in this run" });
            return;
        }

        // A missing or expired token fails the step here, before anything is requested, naming the secret.
        var refreshToken = await context.Secrets.GetSecretAsync(settings.TokenSecret, Name, cancellationToken);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own timeout, set in the client

        var client = new CheckMarxClient(http, options, refreshToken, delay, time)
        {
            OnRetry = message => context.Status.UpdateStep(Name, step => step with { Message = message })
        };

        await new CheckMarxImporter(context, client, options).RunAsync(cancellationToken);
    }

    // Resume and RetryFailed continue an earlier attempt. If its import finished (the marker is written last, after the
    // file is in place) there is nothing to redo. Otherwise the whole import runs again.
    private bool IsAlreadyComplete(RunContext context) =>
        context.Request.Mode is ScanMode.Resume or ScanMode.RetryFailed
        && File.Exists(context.Paths.ImportDoneMarker(Name))
        && File.Exists(context.Paths.ResultFile(Name, CheckMarxImporter.OutputKey));
}
