using System.Text.Json;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>
/// Applies one command from command.json (design section 6.3) and says what happened.
/// "Applied" means the run's control state changed; the tools act on it the next time they look.
/// </summary>
public sealed class CommandProcessor(RunControl control, RunStatusTracker status, TimeProvider? time = null)
{
    public const string Applied = "Applied";
    public const string Rejected = "Rejected";
    public const string Ignored = "Ignored";

    private const int MaxParallelLimit = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public CommandAcknowledgement Apply(CommandEntry command)
    {
        var (result, message) = command.Type switch
        {
            "Pause" => Pause(),
            "Resume" => Resume(),
            "Stop" => Stop(),
            "Abort" => Abort(),
            "SetConcurrency" => SetConcurrency(command.Args),
            "DisableTool" => DisableTool(command.Args),
            "RetryFailed" => RetryFailed(command.Args),
            "SkipRepo" => SkipRepo(command.Args),
            _ => (Rejected, $"unknown command type '{command.Type}'")
        };

        return new CommandAcknowledgement
        {
            Seq = command.Seq,
            Status = result,
            At = _time.GetUtcNow(),
            Message = message
        };
    }

    private (string, string?) Pause()
    {
        if (status.State is not (RunState.Starting or RunState.Running))
        {
            return (Ignored, $"run is {status.State}");
        }

        control.Pause();
        status.SetState(RunState.Paused);
        return (Applied, null);
    }

    private (string, string?) Resume()
    {
        if (!control.IsPaused)
        {
            return (Ignored, "run is not paused");
        }

        control.Resume();
        status.SetState(RunState.Running);
        return (Applied, null);
    }

    private (string, string?) Stop()
    {
        if (status.State is RunState.Stopping)
        {
            return (Ignored, "already stopping");
        }

        control.RequestStop();
        status.SetState(RunState.Stopping);
        return (Applied, null);
    }

    private (string, string?) Abort()
    {
        control.RequestAbort();
        status.SetState(RunState.Stopping);
        return (Applied, null);
    }

    private (string, string?) SetConcurrency(Dictionary<string, JsonElement> args)
    {
        if (!TryGetTool(args, allowAll: true, out var tool, out var problem))
        {
            return (Rejected, problem);
        }

        if (!args.TryGetValue("maxParallel", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var maxParallel)
            || maxParallel is < 1 or > MaxParallelLimit)
        {
            return (Rejected, $"maxParallel must be a whole number from 1 to {MaxParallelLimit}");
        }

        control.SetMaxParallel(tool, maxParallel);
        foreach (var name in tool == "all" ? ToolNames.All : [tool])
        {
            status.SetEffectiveMaxParallel(name, maxParallel);
        }

        return (Applied, null);
    }

    private (string, string?) DisableTool(Dictionary<string, JsonElement> args)
    {
        if (!TryGetTool(args, allowAll: false, out var tool, out var problem))
        {
            return (Rejected, problem);
        }

        var reason = GetString(args, "reason");
        control.DisableTool(tool, reason);
        status.AddAlert(new StatusAlert { Type = "ToolDisabled", Tool = tool, At = _time.GetUtcNow() });
        return (Applied, null);
    }

    private (string, string?) RetryFailed(Dictionary<string, JsonElement> args)
    {
        var tool = GetString(args, "tool");
        if (tool is not null && !ToolNames.All.Contains(tool))
        {
            return (Rejected, $"unknown tool '{tool}'");
        }

        control.RequestRetryFailed(tool);
        return (Applied, null);
    }

    private (string, string?) SkipRepo(Dictionary<string, JsonElement> args)
    {
        var repoKey = GetString(args, "repoKey");
        if (!SafeName.IsValid(repoKey))
        {
            return (Rejected, "repoKey is missing or not a valid repo key");
        }

        control.SkipRepo(repoKey!);
        return (Applied, null);
    }

    private static bool TryGetTool(
        Dictionary<string, JsonElement> args,
        bool allowAll,
        out string tool,
        out string? problem)
    {
        tool = GetString(args, "tool") ?? string.Empty;
        if (allowAll && tool == "all" || ToolNames.All.Contains(tool))
        {
            problem = null;
            return true;
        }

        problem = tool.Length == 0 ? "tool is required" : $"unknown tool '{tool}'";
        return false;
    }

    private static string? GetString(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
