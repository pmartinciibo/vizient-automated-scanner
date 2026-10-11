using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>One repo as a source lists it.</summary>
public sealed record DiscoveredRepo(
    string Key,
    string Source,
    string Org,
    string? Project,
    string Name,
    string CloneUrl,
    RepositoryStatus Status);

/// <summary>A source could not be listed or refused the credential. The message never holds a secret.</summary>
public sealed class RepoSourceException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}
