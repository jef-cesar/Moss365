using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Connections.Ad;

/// <summary>
/// Leest en schrijft on-prem AD via LDAP (System.DirectoryServices). Werkt ook op een pc buiten het domein met een
/// expliciete domeincontroller en een AD-account. Verkeer is ondertekend en versleuteld (Kerberos/NTLM sealing).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdLdap : IAdVerbinding, IUitvoerder
{
    private const AuthenticationTypes Beveiligd = AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

    // HRESULT van "het kenmerk of de waarde bestaat al" (lid van een groep die het al is)
    private const int AlAanwezig = unchecked((int)0x80071392);

    private readonly string _server;
    private readonly string? _gebruiker;
    private readonly string? _wachtwoord;

    public AdLdap(string server, string? gebruiker, string? wachtwoord)
    {
        _server = server;
        _gebruiker = string.IsNullOrWhiteSpace(gebruiker) ? null : gebruiker.Trim();
        _wachtwoord = _gebruiker is null ? null : wachtwoord;
    }

    public Systeem Systeem => Systeem.ActiveDirectory;

    private DirectoryEntry Item(string dn) => new(AdLdapMapper.LdapPad(_server, dn), _gebruiker, _wachtwoord, Beveiligd);

    public string LeesDomeinDn()
    {
        using var root = Item("RootDSE");
        return root.Properties["defaultNamingContext"].Value?.ToString()
               ?? throw new InvalidOperationException("De domeincontroller gaf geen domein terug.");
    }

    public Task<AdGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(null, voortgang, annuleren);

    private const string GebruikerFilter = "(&(objectCategory=person)(objectClass=user))";
    private const string GroepFilter = "(objectClass=group)";

    public Task<AdGegevens> LeesAsync(AdBasis? vorige, IProgress<string>? voortgang, CancellationToken annuleren) => Task.Run(() =>
    {
        voortgang?.Report("AD: verbinden met " + _server + "...");
        var domeinDn = LeesDomeinDn();

        // De stand vóór het lezen: wat tijdens het lezen verandert, komt de volgende keer opnieuw mee
        var stand = LeesStand();
        using var basis = Item(domeinDn);

        if (vorige is not null && stand is not null && AdLdapMapper.KanVerderVan(vorige.Stand, stand))
        {
            var vanaf = $"(uSNChanged>={vorige.Stand.HoogsteUsn + 1})";
            voortgang?.Report("AD: enkel de wijzigingen sinds de vorige lezing...");
            var gewijzigdeGebruikers = Zoek(basis, $"(&{GebruikerFilter}{vanaf})", AdLdapMapper.GebruikerKenmerken, annuleren)
                .Select(AdLdapMapper.NaarGebruiker).ToList();
            var gewijzigdeGroepen = Zoek(basis, $"(&{GroepFilter}{vanaf})", AdLdapMapper.GroepKenmerken, annuleren)
                .Select(AdLdapMapper.NaarGroep).ToList();

            // Alle namen (licht): wat ontbreekt is verwijderd, een andere DN is verplaatst
            var alleGebruikers = Namen(basis, GebruikerFilter, annuleren);
            var alleGroepen = Namen(basis, GroepFilter, annuleren);
            var (g, gr) = AdDelta.Toepassen(vorige, gewijzigdeGebruikers, gewijzigdeGroepen, alleGebruikers, alleGroepen);
            voortgang?.Report($"AD: {gewijzigdeGebruikers.Count} gewijzigde gebruikers en {gewijzigdeGroepen.Count} gewijzigde groepen bijgewerkt ({g.Count} en {gr.Count} in totaal).");
            return new AdGegevens(g, gr, domeinDn, stand, Snel: true);
        }

        voortgang?.Report("AD: gebruikers lezen...");
        var gebruikers = Zoek(basis, GebruikerFilter, AdLdapMapper.GebruikerKenmerken, annuleren)
            .Select(AdLdapMapper.NaarGebruiker).ToList();

        voortgang?.Report("AD: groepen lezen...");
        var groepen = Zoek(basis, GroepFilter, AdLdapMapper.GroepKenmerken, annuleren)
            .Select(AdLdapMapper.NaarGroep).ToList();

        voortgang?.Report($"AD: {gebruikers.Count} gebruikers en {groepen.Count} groepen gelezen.");
        return new AdGegevens(gebruikers, groepen, domeinDn, stand);
    }, annuleren);

    /// <summary>Welke domeincontroller antwoordt en tot waar hij bijgewerkt is; null als dat niet te lezen valt.</summary>
    private AdStand? LeesStand()
    {
        try
        {
            using var root = Item("RootDSE");
            root.RefreshCache(new[] { "dsServiceName", "highestCommittedUSN" });
            return AdLdapMapper.NaarStand(root.Properties["dsServiceName"].Value, root.Properties["highestCommittedUSN"].Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Namen(DirectoryEntry basis, string filter, CancellationToken annuleren)
        => Zoek(basis, filter, new[] { "sAMAccountName", "distinguishedName" }, annuleren)
            .Select(AdLdapMapper.NaarGebruiker)
            .Select(u => (Sam: u.SamAccountName, Dn: u.DistinguishedName))
            .Where(x => x.Sam.Length > 0)
            .GroupBy(x => x.Sam, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Dn, StringComparer.OrdinalIgnoreCase);

    public Task<AdGebruiker?> LeesGebruikerAsync(string samAccountName, CancellationToken annuleren) => Task.Run(() =>
    {
        using var basis = Item(LeesDomeinDn());
        var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={AdLdapMapper.Filterwaarde(samAccountName)}))";
        return Zoek(basis, filter, AdLdapMapper.GebruikerKenmerken, annuleren).Select(AdLdapMapper.NaarGebruiker).FirstOrDefault();
    }, annuleren);

    public Task<AdGroep?> LeesGroepAsync(string samAccountName, CancellationToken annuleren) => Task.Run(() =>
    {
        using var basis = Item(LeesDomeinDn());
        var filter = $"(&(objectClass=group)(sAMAccountName={AdLdapMapper.Filterwaarde(samAccountName)}))";
        return Zoek(basis, filter, AdLdapMapper.GroepKenmerken, annuleren).Select(AdLdapMapper.NaarGroep).FirstOrDefault();
    }, annuleren);

    private static IEnumerable<IReadOnlyDictionary<string, object?[]>> Zoek(DirectoryEntry basis, string filter, string[] kenmerken, CancellationToken annuleren)
    {
        using var zoeker = new DirectorySearcher(basis, filter, kenmerken, SearchScope.Subtree) { PageSize = 500 };
        using var resultaten = zoeker.FindAll();
        var uit = new List<IReadOnlyDictionary<string, object?[]>>();
        foreach (SearchResult r in resultaten)
        {
            annuleren.ThrowIfCancellationRequested();
            var k = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string naam in r.Properties.PropertyNames)
            {
                var waarden = r.Properties[naam];
                var lijst = new object?[waarden.Count];
                waarden.CopyTo(lijst, 0);
                k[naam] = lijst;
            }

            uit.Add(k);
        }

        return uit;
    }

    public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
        => Task.Run<IReadOnlyList<WijzigingResultaat>>(() => wijzigingen.Select(w => Voer(w, annuleren)).ToList(), annuleren);

    private WijzigingResultaat Voer(Wijziging w, CancellationToken annuleren)
    {
        if (annuleren.IsCancellationRequested)
        {
            return WijzigingResultaat.Overslaan(w, "Geannuleerd.");
        }

        try
        {
            switch (w.Actie)
            {
                case Actie.AdGroepLidToevoegen:
                {
                    using var groep = Item(w.P("groepDn"));
                    groep.Properties["member"].Add(w.Doel);
                    groep.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdGroepLidVerwijderen:
                {
                    using var groep = Item(w.P("groepDn"));
                    groep.Properties["member"].Remove(w.Doel);
                    groep.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdKenmerkenZetten:
                {
                    using var item = Item(w.Doel);
                    foreach (var (naam, waarde) in w.Parameters)
                    {
                        if (string.IsNullOrEmpty(waarde))
                        {
                            item.Properties[naam].Clear();
                        }
                        else
                        {
                            item.Properties[naam].Value = waarde;
                        }
                    }

                    item.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdGebruikerAanmaken:
                    return Aanmaken(w);

                case Actie.AdGroepAanmaken:
                {
                    using var ou = Item(w.P("ou"));
                    using var groep = ou.Children.Add("CN=" + Core.Wijzigingen.PlanBouwer.EscapeCn(w.P("cn")), "group");
                    foreach (var (naam, waarde) in w.Parameters)
                    {
                        if (naam is "ou" or "cn" || string.IsNullOrWhiteSpace(waarde))
                        {
                            continue;
                        }

                        groep.Properties[naam].Value = naam == "groupType" ? int.Parse(waarde, System.Globalization.CultureInfo.InvariantCulture) : waarde;
                    }

                    groep.CommitChanges();
                    return WijzigingResultaat.Ok(w, "Aangemaakt.");
                }

                case Actie.AdAccountUitschakelen:
                {
                    using var item = Item(w.Doel);
                    var uac = Convert.ToInt32(item.Properties["userAccountControl"].Value);
                    item.Properties["userAccountControl"].Value = uac | AdLdapMapper.UacUitgeschakeld;
                    item.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdAccountInschakelen:
                {
                    using var item = Item(w.Doel);
                    var uac = Convert.ToInt32(item.Properties["userAccountControl"].Value);
                    item.Properties["userAccountControl"].Value = uac & ~AdLdapMapper.UacUitgeschakeld;
                    item.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdAccountOntgrendelen:
                {
                    using var item = Item(w.Doel);
                    item.Properties["lockoutTime"].Value = 0;
                    item.CommitChanges();
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.AdWachtwoordInstellen:
                    return WachtwoordInstellen(w);

                case Actie.AdVerplaatsen:
                {
                    using var item = Item(w.Doel);
                    using var doel = Item(w.P("doelOu"));
                    item.MoveTo(doel);
                    return WijzigingResultaat.Ok(w);
                }

                default:
                    return WijzigingResultaat.Fout(w, $"Actie {w.Actie} hoort niet bij AD.");
            }
        }
        catch (COMException ex) when (ex.HResult == AlAanwezig && w.Actie == Actie.AdGroepLidToevoegen)
        {
            return WijzigingResultaat.Ok(w, "Was al lid.");
        }
        catch (Exception ex)
        {
            return WijzigingResultaat.Fout(w, ex.Message.Trim());
        }
    }

    /// <summary>Nieuw wachtwoord (uit Geheim, nooit gelogd); eventueel wijzigen bij de volgende aanmelding.</summary>
    private WijzigingResultaat WachtwoordInstellen(Wijziging w)
    {
        if (string.IsNullOrEmpty(w.Geheim))
        {
            return WijzigingResultaat.Fout(w, "Geen wachtwoord meegegeven (het leeft enkel in het geheugen; maak het plan opnieuw).");
        }

        using var item = Item(w.Doel);
        try
        {
            item.Invoke("SetPassword", w.Geheim);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Bv. het wachtwoord voldoet niet aan het wachtwoordbeleid van het domein
            return WijzigingResultaat.Fout(w, ex.InnerException.Message.Trim());
        }

        var wijzigen = string.Equals(w.P("wijzigenBijAanmelding"), "True", StringComparison.OrdinalIgnoreCase);
        item.Properties["pwdLastSet"].Value = wijzigen ? 0 : -1;
        item.CommitChanges();
        return WijzigingResultaat.Ok(w, wijzigen ? "Ingesteld; wijzigen bij de volgende aanmelding." : "Ingesteld.");
    }

    private WijzigingResultaat Aanmaken(Wijziging w)
    {
        if (string.IsNullOrEmpty(w.Geheim))
        {
            return WijzigingResultaat.Fout(w, "Geen startwachtwoord: het account is niet aangemaakt.");
        }

        using var ou = Item(w.P("ou"));
        using var gebruiker = ou.Children.Add("CN=" + Core.Wijzigingen.PlanBouwer.EscapeCn(w.P("cn")), "user");
        foreach (var (naam, waarde) in w.Parameters)
        {
            if (naam is "ou" or "cn" || string.IsNullOrWhiteSpace(waarde))
            {
                continue;
            }

            gebruiker.Properties[naam].Value = waarde;
        }

        gebruiker.CommitChanges();

        try
        {
            gebruiker.Invoke("SetPassword", w.Geheim);
            gebruiker.Properties["userAccountControl"].Value = AdLdapMapper.UacNormaalAccount;
            gebruiker.Properties["pwdLastSet"].Value = 0; // wachtwoord wijzigen bij de eerste aanmelding
            gebruiker.CommitChanges();
        }
        catch (Exception ex)
        {
            var reden = ex.InnerException?.Message ?? ex.Message;
            return WijzigingResultaat.Fout(w, $"Account aangemaakt maar nog uitgeschakeld: wachtwoord instellen mislukt ({reden.Trim()}).");
        }

        return WijzigingResultaat.Ok(w, "Aangemaakt en ingeschakeld.");
    }
}
