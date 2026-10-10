using System.Globalization;
using Vizient.AutomatedScanner.Core.Configuration;

namespace Vizient.AutomatedScanner.Core.Secrets;

/// <summary>What the secrets commands need from the terminal, so they can be run and tested without one.</summary>
public interface ISecretsConsole
{
    TextWriter Out { get; }
    TextWriter Error { get; }

    /// <summary>The Windows account the engine is running as, as DOMAIN\name.</summary>
    string CurrentAccount { get; }

    /// <summary>Asks for a value and reads it without showing it. Null if there is no interactive console to ask on.</summary>
    string? ReadSecretWithoutEcho(string prompt);

    /// <summary>One line from standard input, for piping a value in. Null if there is none.</summary>
    string? ReadStdinLine();
}

/// <summary>
/// engine secrets set | list | remove | check (design section 12). Values are never printed. There is intentionally
/// no way to give a value on the command line, where it would show in the process list and shell history.
/// Exit codes: 0 ok, 1 warnings only, 2 error.
/// </summary>
public static class SecretsCommand
{
    public const string Usage = """
        Usage:
          engine secrets set <name> [--expires yyyy-MM-dd] [--stdin] [--file <path>]
          engine secrets list [--file <path>]
          engine secrets remove <name> [--file <path>]
          engine secrets check [--file <path>]

        --stdin reads the value from standard input instead of asking for it.
        --file works on that secrets file instead of secrets.dat beside the engine.
        """;

    /// <param name="args">The arguments after "secrets".</param>
    public static async Task<int> RunAsync(
        string[] args,
        EngineConfig config,
        ISecretsConsole io,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        time ??= TimeProvider.System;

        if (!TryParse(args, out var options, out var problem))
        {
            io.Error.WriteLine(problem);
            io.Error.WriteLine(Usage);
            return 2;
        }

        var file = options.File is null ? config.SecretsFile : Path.GetFullPath(options.File);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        io.Out.WriteLine($"Secrets file: {file}");

        try
        {
            return options.Command switch
            {
                "set" => await SetAsync(options, file, config, io, time, cancellationToken),
                "remove" => await RemoveAsync(options, file, config, io, cancellationToken),
                "list" => await ListAsync(file, config, io, today, cancellationToken),
                _ => await CheckAsync(file, config, io, today, cancellationToken)
            };
        }
        catch (SecretsFileException ex)
        {
            io.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    // ---- set and remove ----

    private static async Task<int> SetAsync(
        Options options, string file, EngineConfig config, ISecretsConsole io, TimeProvider time, CancellationToken ct)
    {
        if (!AccountAllowed(config, io))
        {
            return 2;
        }

        var name = options.Name!;
        var value = options.Stdin
            ? io.ReadStdinLine()
            : io.ReadSecretWithoutEcho($"Value for secret '{name}': ");

        if (value is null)
        {
            io.Error.WriteLine(options.Stdin
                ? "Error: nothing was provided on standard input."
                : "Error: there is no interactive console to ask on. Pipe the value in and use --stdin.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            io.Error.WriteLine("Error: a secret cannot be empty.");
            return 2;
        }

        var store = await SecretStore.LoadAsync(file, ct);
        var replacing = store.Contains(name);
        try
        {
            store.Set(name, value, options.Expires, time.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            io.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        await store.SaveAsync(file, ct);
        var expiry = options.Expires is { } date ? $", expires {date:yyyy-MM-dd}" : ", does not expire";
        io.Out.WriteLine($"{(replacing ? "Replaced" : "Saved")} secret '{name}'{expiry}.");
        return 0;
    }

    private static async Task<int> RemoveAsync(Options options, string file, EngineConfig config, ISecretsConsole io, CancellationToken ct)
    {
        if (!AccountAllowed(config, io))
        {
            return 2;
        }

        var store = await SecretStore.LoadAsync(file, ct);
        if (!store.Remove(options.Name!))
        {
            io.Error.WriteLine($"Error: there is no secret named '{options.Name}'.");
            return 2;
        }

        await store.SaveAsync(file, ct);
        io.Out.WriteLine($"Removed secret '{options.Name}'.");
        return 0;
    }

    private static bool AccountAllowed(EngineConfig config, ISecretsConsole io)
    {
        var required = config.Current.Secrets.RequiredAccount;
        if (required is null || string.Equals(required, io.CurrentAccount, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The file can only be read by the account that wrote it, so one written by another account is no use.
        io.Error.WriteLine($"Error: this must be run as {required}, but you are {io.CurrentAccount}.");
        return false;
    }

    // ---- list and check ----

    private static async Task<int> ListAsync(string file, EngineConfig config, ISecretsConsole io, DateOnly today, CancellationToken ct)
    {
        var store = await SecretStore.LoadAsync(file, ct);
        if (store.Count == 0)
        {
            io.Out.WriteLine(File.Exists(file) ? "There are no secrets in the file." : "The file does not exist yet.");
            return 0;
        }

        var rows = store.List(today, config.Current.Secrets.ExpiryWarnDays);
        var width = Math.Max(4, rows.Max(r => r.Name.Length));
        io.Out.WriteLine($"{"NAME".PadRight(width)}  {"CREATED",-20}  {"EXPIRES",-10}  STATUS");
        foreach (var row in rows)
        {
            var created = row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var expires = row.ExpiresAt is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never";
            var status = row.Status switch
            {
                SecretStatus.Expired => $"EXPIRED ({-row.DaysLeft} days ago)",
                SecretStatus.ExpiringSoon => $"expiring in {row.DaysLeft} days",
                _ => "ok"
            };
            io.Out.WriteLine($"{row.Name.PadRight(width)}  {created,-20}  {expires,-10}  {status}");
        }

        return 0;
    }

    private static async Task<int> CheckAsync(string file, EngineConfig config, ISecretsConsole io, DateOnly today, CancellationToken ct)
    {
        var store = await SecretStore.LoadAsync(file, ct);
        if (!File.Exists(file))
        {
            io.Out.WriteLine("The file does not exist yet, so every secret engine.json names is missing.");
        }

        var settings = config.Current;
        var result = SecretsChecker.Check(settings, store, tool => settings.Tool(tool).Enabled != false, today);

        foreach (var name in result.Ok)
        {
            io.Out.WriteLine($"OK        {name}");
        }

        foreach (var finding in result.Findings)
        {
            var detail = finding.Issue switch
            {
                SecretIssue.Missing => "has not been set",
                SecretIssue.Expired => $"expired on {finding.ExpiresAt:yyyy-MM-dd}",
                _ => $"expires {finding.ExpiresAt:yyyy-MM-dd} ({finding.DaysLeft} days)"
            };
            var label = finding.Issue switch
            {
                SecretIssue.Missing => "MISSING",
                SecretIssue.Expired => "EXPIRED",
                _ => "EXPIRING"
            };
            io.Out.WriteLine($"{label,-9} {finding.Name}: {detail}. Needed by {finding.RequiredBy}.");
        }

        var errors = result.Findings.Count(f => f.Issue != SecretIssue.ExpiringSoon);
        io.Out.WriteLine($"{result.Ok.Count} ok, {result.Findings.Count - errors} warnings, {errors} errors.");
        return result.ExitCode;
    }

    // ---- reading the command line ----

    private sealed record Options(string Command, string? Name, DateOnly? Expires, bool Stdin, string? File);

    private static bool TryParse(string[] args, out Options options, out string problem)
    {
        options = new Options("", null, null, false, null);
        problem = string.Empty;

        if (args.Length == 0 || args[0] is not ("set" or "list" or "remove" or "check"))
        {
            problem = args.Length == 0 ? "Error: say what to do with the secrets." : $"Error: '{args[0]}' is not a secrets command.";
            return false;
        }

        string? name = null, file = null;
        DateOnly? expires = null;
        var stdin = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdin":
                    stdin = true;
                    break;
                case "--expires" when i + 1 < args.Length:
                    if (!DateOnly.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        problem = $"Error: '{args[i]}' is not a date. Use yyyy-MM-dd.";
                        return false;
                    }

                    expires = date;
                    break;
                case "--file" when i + 1 < args.Length:
                    file = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || name is not null)
                    {
                        problem = args[i].StartsWith("--", StringComparison.Ordinal)
                            ? $"Error: '{args[i]}' is not an option of 'secrets {args[0]}'."
                            : $"Error: unexpected '{args[i]}'.";
                        return false;
                    }

                    name = args[i];
                    break;
            }
        }

        var command = args[0];
        if (command is "set" or "remove" && name is null)
        {
            problem = $"Error: 'secrets {command}' needs the name of a secret.";
            return false;
        }

        if (command is "list" or "check" && name is not null)
        {
            problem = $"Error: 'secrets {command}' takes no name.";
            return false;
        }

        if (command != "set" && (stdin || expires is not null))
        {
            problem = $"Error: --stdin and --expires belong to 'secrets set', not 'secrets {command}'.";
            return false;
        }

        options = new Options(command, name, expires, stdin, file);
        return true;
    }
}
