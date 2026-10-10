using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Work;

/// <summary>
/// Decides from what RepoClone reported whether a scanner can and should run on a repo. Anything that is
/// not clearly excluded is scanned: a doubt means scan.
/// </summary>
public static class RepoEligibility
{
    public const string NotApplicable = "NotApplicable";
    public const string NoLocalClone = "NoLocalClone";

    /// <summary>A marker saying why the repo is skipped, or null if it should be scanned.</summary>
    public static ToolSkippedMarker? SkipFor(RepoOutcome repo, bool scanArchived = false, Func<RepoOutcome, bool>? isApplicable = null)
    {
        var statusReason = repo.RemoteStatus switch
        {
            RepositoryStatus.Archived when !scanArchived => "StatusArchived",
            RepositoryStatus.Disabled => "StatusDisabled",
            RepositoryStatus.Deleted => "StatusDeleted",
            RepositoryStatus.Unreachable => "StatusUnreachable",
            _ => null
        };

        if (statusReason is not null)
        {
            return new ToolSkippedMarker { Reason = statusReason };
        }

        if (isApplicable is not null && !isApplicable(repo))
        {
            return new ToolSkippedMarker { Reason = NotApplicable };
        }

        // A repo whose clone failed (or was never made) has nothing on disk to scan.
        if (string.IsNullOrWhiteSpace(repo.LocalPath) || !(repo.ExistedLocally || repo.UpdatedLocally))
        {
            return new ToolSkippedMarker { Reason = NoLocalClone };
        }

        return null;
    }
}
