using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Permissions;
using Moss365.Core.Security;

namespace Moss365.UI.Services;

/// <summary>Light access reviews (R20): the settings per subject, finishing a review (the decisions to the change log)
/// and the plans that follow from it.</summary>
public sealed partial class ManagementState
{
    public IReadOnlyList<ReviewSetting> ReviewSettings() => AccessReviews.Read(Database.ReadDocument(AccessReviews.Document));

    /// <summary>Everything to review in the current data, with when it is due.</summary>
    public IReadOnlyList<ReviewDue> AccessReviewsDue()
        => AccessReviews.Due(AccessReviews.Subjects(Data, IsHistoric ? null : SecurityCheckResults), ReviewSettings(), DateTimeOffset.Now);

    /// <summary>The reviewer or the interval of a subject (needs the role for managing).</summary>
    public void SaveReviewSetting(ReviewSetting setting)
    {
        RefuseReviewChanges();
        Database.SaveDocument(AccessReviews.Document, AccessReviews.Write(AccessReviews.With(ReviewSettings(), setting)));
        Changed?.Invoke();
    }

    /// <summary>
    /// Finishes a review: every decision goes to the change log and the date of the review is kept. Returns the plan of
    /// what is to be removed, which still has to be confirmed like every plan.
    /// </summary>
    public PlanOutcome FinishReview(ReviewSubject subject, IReadOnlyCollection<ReviewDecision> decisions)
    {
        RefuseReviewChanges();
        var now = DateTimeOffset.Now;
        var settings = ReviewSettings();
        var setting = settings.FirstOrDefault(s => string.Equals(s.Subject, subject.Id, StringComparison.OrdinalIgnoreCase)) ?? new ReviewSetting(subject.Id, subject.DefaultReviewer);
        foreach (var line in AccessReviews.LogLines(subject, decisions, User, setting.Reviewer, now))
        {
            ChangeLog.Write(line);
        }

        Database.SaveDocument(AccessReviews.Document, AccessReviews.Write(AccessReviews.With(settings, setting with { Reviewed = now, ReviewedBy = User })));
        ForgetKpiCounts();
        Log.Information("Toegangsreview afgerond: {Behouden} behouden, {Weg} te verwijderen", decisions.Count(d => d.Keep), decisions.Count(d => !d.Keep));
        Changed?.Invoke();
        return AccessReviews.Removals(subject, decisions);
    }

    private void RefuseReviewChanges()
    {
        if (IsHistoric || !Can(Permission.Manage))
        {
            throw new InvalidOperationException(Texts.T["access-reviews.not-allowed"]);
        }
    }
}
