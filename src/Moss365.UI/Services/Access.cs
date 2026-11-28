using Moss365.Core.Permissions;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>Which permission a page needs (the first of the three places: pages, buttons, the engine).</summary>
public static class Access
{
    /// <summary>The permission for an address (without a search term), or null when everyone may go there.</summary>
    public static Permission? ForPath(string path)
    {
        var p = Pending.Normalise(path).ToLowerInvariant();
        var first = p.Split('/')[0];
        return first switch
        {
            // Connecting and the check of this PC: everyone (signing in happens per PC)
            "" or "verbindingen" or "controle" or "taal" or "inventaris" => null,
            "groepen" when p == "groepen/nieuw" => Permission.Manage,
            "statistieken" or "gebruikers" or "gebruiker" or "gasten" or "groepen" or "groep" or "postvak" or "room" or "rooms" or "vergelijk" => Permission.View,
            "postvakken" => p == "postvakken/nieuw" ? Permission.Manage : Permission.View,
            "contacten" or "contact" => Permission.View,
            "beveiliging" or "conditional-access" or "controles" => Permission.Security,
            "bewaarde-logboeken" => Permission.SignIns,
            "toegangsreviews" => Permission.Security,
            "spiegel" or "functieprofielen" => Permission.CompareMirror,
            "nieuw" or "uitstroom" or "gepland" or "opvolging" => Permission.Actions,
            "opkuis" => Permission.Manage,
            "logonscripts" or "toestellen" or "samenwerking" or "rapporten" or "aanvragen" => Permission.View,
            "gastenverlenging" => Permission.InviteGuests,
            "sleutels" or "wachtwoorden" => Permission.Helpdesk,
            "scripts" => Permission.Scripts,
            "logboek" => Permission.ChangeLogOwn,
            "organisatie" or "tenants" or "tenantvergelijking" or "inrichten" or "databank" or "lezen-zonder-venster" or "alle-tenants" or "postlocatie" or "koppelingen" or "bron" => Permission.Settings,
            _ => Permission.View,
        };
    }

    /// <summary>
    /// The page fits the systems of the tenant: pages about Microsoft 365 only for a tenant with Microsoft 365, the
    /// comparison of AD and Entra only for a tenant with both.
    /// </summary>
    public static bool FitsTenant(string path, bool hasAd, bool hasMicrosoft365)
        => FitsTenant(path, (hasAd ? TenantSystems.ActiveDirectory : TenantSystems.None) | (hasMicrosoft365 ? TenantSystems.Microsoft365 : TenantSystems.None));

    /// <summary>
    /// The page fits the systems of the tenant (G0: any combination of AD, Microsoft 365 and Google Workspace): the menu,
    /// the settings and Ctrl+K only show what the tenant has. A page about one system needs that system; linking persons
    /// and the source of truth need at least two systems.
    /// </summary>
    public static bool FitsTenant(string path, TenantSystems systems)
    {
        var hasAd = systems.HasFlag(TenantSystems.ActiveDirectory);
        var hasMicrosoft365 = systems.HasFlag(TenantSystems.Microsoft365);
        var first = path.Split('?', '#')[0].Split('/')[0];
        if (first is "koppelingen" or "bron")
        {
            return TenantSystemsInfo.Each.Count(s => systems.HasFlag(s)) >= 2;
        }

        return first switch
        {
            "gasten" or "postvakken" or "postvak" or "rooms" or "room" or "contacten" or "contact" or "beveiliging" or "conditional-access" or "inrichten" or "bewaarde-logboeken" => hasMicrosoft365,
            "vergelijk" => hasAd && hasMicrosoft365,
            "logonscripts" or "wachtwoorden" => hasAd,
            "licenties" or "samenwerking" or "gastenverlenging" => hasMicrosoft365,
            "nieuw" => hasAd || hasMicrosoft365, // in AD, or in the cloud (0.31)
            _ => true,
        };
    }
}
