using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>Job profiles (R9, 0.22): who got which profile, kept in the tenant database.</summary>
public sealed partial class ManagementState
{
    public Dictionary<string, ProfileAssignment> ProfileAssignments()
        => JobProfilePlans.ReadAssignments(Database.ReadDocument(Documents.JobProfileAssignments));

    /// <summary>Remembers that the person got this version of the profile (after the plan ran); not in the past.</summary>
    public void SaveProfileAssignment(Person person, JobProfile profile)
    {
        if (IsHistoric)
        {
            return;
        }

        var all = ProfileAssignments();
        all[person.Key] = new ProfileAssignment(profile.Id, profile.Version, DateTimeOffset.Now, User);
        Database.SaveDocument(Documents.JobProfileAssignments, JobProfilePlans.WriteAssignments(all));
    }
}
