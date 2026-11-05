using Moss365.Core.Permissions;

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
            "" or "verbindingen" or "controle" => null,
            "groepen" when p == "groepen/nieuw" => Permission.Manage,
            "statistieken" or "gebruikers" or "gebruiker" or "gasten" or "groepen" or "groep" or "postvak" or "room" or "rooms" or "vergelijk" => Permission.View,
            "postvakken" => p == "postvakken/nieuw" ? Permission.Manage : Permission.View,
            "beveiliging" or "conditional-access" => Permission.Security,
            "spiegel" => Permission.CompareMirror,
            "nieuw" or "uitstroom" or "gepland" or "opvolging" => Permission.Actions,
            "opkuis" => Permission.Manage,
            "scripts" => Permission.Scripts,
            "logboek" => Permission.ChangeLogOwn,
            "organisatie" => Permission.Settings,
            _ => Permission.View,
        };
    }
}
