using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>One tenant on All tenants (R19, U20): locked, or its last counts (null when it has none yet).</summary>
public sealed record TenantSummary(TenantInfo Tenant, bool Locked, TenantCounts? Counts);

/// <summary>
/// All tenants (R19, U20): per tenant only scores and counts, side by side, read when shown from each tenant's own
/// database (unlocked tenants only). Nothing is stored or exported together, no state of a tenant is opened for it, and
/// no name of a person or object leaves a tenant.
/// </summary>
public sealed partial class TenantHub
{
    public IReadOnlyList<TenantSummary> Summaries(IReadOnlyCollection<string> keys)
        => App.Tenants().Select(t =>
        {
            if (!TryKey(t, out var key))
            {
                return new TenantSummary(t, true, null);
            }

            try
            {
                return new TenantSummary(t, false, Database.ReadCounts(TenantFolders.DatabasePath(_environment.Root, t.Id), key, keys));
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return new TenantSummary(t, false, null);
            }
        }).ToList();
}
