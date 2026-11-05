using Moss365.Core.Analyse;

namespace Moss365.Core.Inventaris;

public enum ObjectSoort
{
    Gebruiker,
    Groep,
    Postvak,
    Room,
    Apparaat,
    Contact,
    Onbekend,
}

/// <summary>Verwijzing naar een object met een eigen pagina in de app (of een herkenbare naam als het niet gekend is).</summary>
public sealed record ObjectRef(ObjectSoort Soort, string Sleutel, string Naam, string Detail = "")
{
    /// <summary>Relatief pad naar de pagina, of null als het object geen pagina heeft.</summary>
    public string? Route => Soort switch
    {
        ObjectSoort.Gebruiker => "gebruiker/" + Uri.EscapeDataString(Sleutel),
        ObjectSoort.Groep => "groep/" + Uri.EscapeDataString(Sleutel),
        ObjectSoort.Postvak => "postvak/" + Uri.EscapeDataString(Sleutel),
        ObjectSoort.Room => "room/" + Uri.EscapeDataString(Sleutel),
        _ => null,
    };

    public string SoortTekst => Soort switch
    {
        ObjectSoort.Gebruiker => "gebruiker",
        ObjectSoort.Groep => "groep",
        ObjectSoort.Postvak => "postvak",
        ObjectSoort.Room => "room",
        ObjectSoort.Apparaat => "apparaat",
        ObjectSoort.Contact => "contactpersoon",
        _ => "onbekend",
    };

    public bool IsZelfde(ObjectRef? ander) => ander is not null && ander.Soort == Soort && string.Equals(ander.Sleutel, Sleutel, StringComparison.OrdinalIgnoreCase);

    public static ObjectRef Onbekend(string naam, string detail = "") => new(ObjectSoort.Onbekend, string.Empty, naam, detail);
}

/// <summary>
/// Een groep over de lagen heen: de AD-groep, de groep in Entra en/of in Exchange. Een uit AD gesynchroniseerde groep
/// is één groep met een AD- en een cloudkant.
/// </summary>
public sealed class Groep
{
    public required string Sleutel { get; init; }
    public AdGroep? Ad { get; internal set; }
    public CloudGroep? Cloud { get; internal set; }
    public ExoGroep? Exo { get; internal set; }

    public string Naam => First(Cloud?.DisplayName, Exo?.DisplayName, Ad?.DisplayName, Ad?.Name);
    public string Mail => First(Cloud?.Mail, Exo?.Smtp, Ad?.Mail);
    public string Omschrijving => First(Ad?.Description, Cloud?.Description);
    public bool Dynamisch => Cloud?.Dynamisch == true;

    /// <summary>Langs waar leden en kenmerken gewijzigd worden.</summary>
    public SchrijfRoute Route => Dynamisch ? SchrijfRoute.Geen
        : Ad is not null ? SchrijfRoute.ActiveDirectory
        : Cloud is not null ? Spiegelgebruiker.RouteVoor(Cloud)
        : Exo is not null ? SchrijfRoute.Exchange
        : SchrijfRoute.Geen;

    public string SoortTekst
    {
        get
        {
            if (Ad is not null)
            {
                var soort = $"AD-{(string.Equals(Ad.Category, "Distribution", StringComparison.OrdinalIgnoreCase) ? "distributiegroep" : "beveiligingsgroep")}";
                return Cloud is not null ? soort + " (gesynchroniseerd naar Entra)" : soort;
            }

            if (Cloud is not null)
            {
                return Cloud.TypeTekst + (Dynamisch ? " (dynamisch)" : string.Empty);
            }

            return Exo?.TypeTekst ?? "Groep";
        }
    }

    public string Lagen => string.Join(" + ", new[] { Ad is null ? null : "AD", Cloud is null ? null : "Entra", Exo is null ? null : "Exchange" }.Where(l => l is not null));

    public ObjectRef Ref => new(ObjectSoort.Groep, Sleutel, Naam, SoortTekst);

    private static string First(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}

/// <summary>Een lid of ouder van een groep, met de laag waarin de relatie bestaat.</summary>
public sealed record Relatie(string Laag, ObjectRef Object);

/// <summary>Een recht op een postvak met de herkende rechthebbende.</summary>
public sealed record RechtRegel(Postvak Postvak, string Recht, string Trustee, ObjectRef Wie, ObjectRef? ViaGroep = null)
{
    /// <summary>Rechtstreeks op een persoon in plaats van via een groep.</summary>
    public bool Direct => Wie.Soort != ObjectSoort.Groep;
}

/// <summary>
/// Zoekt objecten op naam, adres, DN of id en legt de relaties tussen gebruikers, groepen, postvakken en rooms.
/// Wordt eenmaal per gegevensset opgebouwd (zie <see cref="InventarisData.Index"/>).
/// </summary>
public sealed class ObjectIndex
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private readonly InventarisData _data;
    private readonly Dictionary<string, ObjectRef> _sterk = new(Cmp);
    private readonly Dictionary<string, ObjectRef> _zwak = new(Cmp);
    private readonly Dictionary<string, Groep> _groepen = new(Cmp);
    private readonly Dictionary<string, Postvak> _postvakken = new(Cmp);
    private readonly Dictionary<string, Room> _rooms = new(Cmp);
    private readonly Dictionary<string, List<string>> _cloudLeden = new(Cmp);
    private readonly Dictionary<string, List<Groep>> _adOuders = new(Cmp);
    private readonly Dictionary<string, List<Groep>> _exoOuders = new(Cmp);
    private readonly List<RechtRegel> _rechten = new();

    public ObjectIndex(InventarisData data)
    {
        _data = data;
        BouwGroepen();
        BouwZoektabel();
        BouwRelaties();
    }

    public IReadOnlyCollection<Groep> Groepen => _groepen.Values;
    public IReadOnlyList<RechtRegel> Rechten => _rechten;

    public Groep? Groep(string sleutel) => _groepen.GetValueOrDefault(sleutel);
    public Postvak? Postvak(string smtp) => _postvakken.GetValueOrDefault(smtp);
    public Room? Room(string smtp) => _rooms.GetValueOrDefault(smtp);

    /// <summary>
    /// De object-ID in Entra (Azure) van een gebruiker, cloudgroep, of het account van een postvak of room; null als het
    /// object niet in de geladen Entra-gegevens staat (bv. een groep die enkel in AD of Exchange bestaat).
    /// </summary>
    public string? EntraIdVan(ObjectRef r)
    {
        var id = r.Soort switch
        {
            ObjectSoort.Gebruiker => _data.ZoekPersoon(r.Sleutel)?.Entra?.Id,
            ObjectSoort.Groep => Groep(r.Sleutel)?.Cloud?.Id,
            ObjectSoort.Postvak => Postvak(r.Sleutel) is { } p ? AccountVan(p.Smtp, p.Alias)?.Entra?.Id : null,
            ObjectSoort.Room => Room(r.Sleutel) is { } k ? AccountVan(k.Smtp, k.Alias)?.Entra?.Id : null,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private HashSet<string>? _postvakAliassen;

    /// <summary>
    /// Is dit account eigenlijk een postvak (gedeeld postvak, room, planningspostvak)? Zulke accounts hebben ook een
    /// gebruiker in Entra (en soms in AD), maar niemand meldt er zich mee aan: ze horen niet in de cijfers over
    /// medewerkers, aanmeldingen of MFA. Herkend aan het adres of de alias.
    /// </summary>
    public bool IsPostvakAccount(Persoon p)
    {
        foreach (var adres in new[] { p.Entra?.Mail, p.Entra?.UserPrincipalName, p.Ad?.EmailAddress, p.Ad?.UserPrincipalName })
        {
            if (!string.IsNullOrWhiteSpace(adres) && (_postvakken.ContainsKey(adres.Trim()) || _rooms.ContainsKey(adres.Trim())))
            {
                return true;
            }
        }

        _postvakAliassen ??= _data.Postvakken.Select(x => x.Alias).Concat(_data.Rooms.Select(r => r.Alias))
            .Where(a => !string.IsNullOrWhiteSpace(a)).ToHashSet(Cmp);
        return p.Entra?.MailNickname is { Length: > 0 } alias && _postvakAliassen.Contains(alias);
    }

    /// <summary>
    /// Het account (AD en/of Entra) achter een postvak of room: het account met dat adres, of met dezelfde alias.
    /// Null als het account niet geladen is.
    /// </summary>
    public Persoon? AccountVan(string smtp, string? alias = null)
    {
        _accountOpAdres ??= BouwAccountsOpAdres();
        if (_accountOpAdres.TryGetValue(smtp.Trim(), out var p))
        {
            return p;
        }

        return alias is { Length: > 0 } a
            ? _data.Personen.FirstOrDefault(x => string.Equals(x.Entra?.MailNickname, a, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private Dictionary<string, Persoon>? _accountOpAdres;

    private Dictionary<string, Persoon> BouwAccountsOpAdres()
    {
        var uit = new Dictionary<string, Persoon>(Cmp);
        foreach (var p in _data.Personen)
        {
            foreach (var adres in new[] { p.Entra?.Mail, p.Entra?.UserPrincipalName, p.Ad?.EmailAddress, p.Ad?.UserPrincipalName })
            {
                if (!string.IsNullOrWhiteSpace(adres))
                {
                    uit.TryAdd(adres.Trim(), p);
                }
            }
        }

        return uit;
    }

    public Groep? GroepVan(AdGroep g) => Zoek(g.DistinguishedName) is { Soort: ObjectSoort.Groep } r ? Groep(r.Sleutel) : null;
    public Groep? GroepVan(CloudGroep g) => Zoek(g.Id) is { Soort: ObjectSoort.Groep } r ? Groep(r.Sleutel) : null;

    /// <summary>Herkent een naam, adres, DN, id of DOMEIN\naam. Adressen en DN's gaan voor op weergavenamen.</summary>
    public ObjectRef? Zoek(string? identiteit)
    {
        var s = identiteit?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        if (_sterk.TryGetValue(s, out var r) || _zwak.TryGetValue(s, out r))
        {
            return r;
        }

        var slash = s.IndexOf('\\');
        if (slash > 0 && _sterk.TryGetValue(s[(slash + 1)..], out r))
        {
            return r;
        }

        return null;
    }

    /// <summary>Zoals <see cref="Zoek"/>, maar altijd een verwijzing: onbekende waarden blijven leesbaar.</summary>
    public ObjectRef Ref(string identiteit, string detail = "")
        => Zoek(identiteit) ?? ObjectRef.Onbekend(identiteit,
            detail.Length > 0 ? detail : identiteit.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase) ? "verwijderd account (SID)" : "niet gevonden");

    public ObjectRef Ref(Persoon p) => new(ObjectSoort.Gebruiker, p.Sleutel, p.Naam, p.Upn);

    // ------------------------------------------------------------------ relaties van een groep

    public IReadOnlyList<Relatie> LedenVan(Groep g)
    {
        var uit = new List<Relatie>();
        if (g.Ad is { } ad)
        {
            var dns = ad.LedenBekend
                ? ad.Leden
                : _data.AdGebruikers.Where(u => u.MemberOf.Contains(ad.DistinguishedName, Cmp)).Select(u => u.DistinguishedName).ToList();
            uit.AddRange(dns.Distinct(Cmp).Select(dn => new Relatie("AD", Zoek(dn) ?? ObjectRef.Onbekend(DnHulp.Naam(dn), DnHulp.OuPadVanObject(dn)))));
        }

        if (g.Cloud is { } cloud && _cloudLeden.TryGetValue(cloud.Id, out var ids))
        {
            uit.AddRange(ids.Distinct(Cmp).Select(id => new Relatie("Entra", CloudLid(id))));
        }

        if (g.Exo is { } exo)
        {
            uit.AddRange(exo.Leden.Select(l => new Relatie("Exchange", Zoek(l.Smtp) ?? Zoek(l.Naam) ?? ObjectRef.Onbekend(First(l.Naam, l.Smtp), l.Smtp))));
        }

        return Sorteer(uit);
    }

    /// <summary>Zoals <see cref="LedenVan"/>, maar een lid dat in meerdere lagen staat (bv. AD en Entra) één keer, met alle lagen.</summary>
    public IReadOnlyList<Relatie> UniekeLedenVan(Groep g)
        => LedenVan(g)
            .GroupBy(r => r.Object.Soort == ObjectSoort.Onbekend ? "?" + r.Object.Naam : r.Object.Soort + ":" + r.Object.Sleutel, Cmp)
            .Select(x => new Relatie(string.Join(", ", x.Select(r => r.Laag).Distinct()), x.First().Object))
            .ToList();

    /// <summary>De groepen waarvan deze groep zelf lid is (geneste groepen).</summary>
    public IReadOnlyList<Relatie> LidVan(Groep g)
    {
        var uit = new List<Relatie>();
        if (g.Ad is { } ad && _adOuders.TryGetValue(ad.DistinguishedName, out var adOuders))
        {
            uit.AddRange(adOuders.Select(o => new Relatie("AD", o.Ref)));
        }

        if (g.Cloud is { } cloud && _data.CloudLidmaatschap.TryGetValue(cloud.Id, out var cloudOuders))
        {
            uit.AddRange(cloudOuders.Select(id => Zoek(id)).OfType<ObjectRef>().Select(r => new Relatie("Entra", r)));
        }

        if (g.Mail.Length > 0 && _exoOuders.TryGetValue(g.Mail, out var exoOuders))
        {
            uit.AddRange(exoOuders.Select(o => new Relatie("Exchange", o.Ref)));
        }

        return Sorteer(uit);
    }

    /// <summary>Beheerders: managedBy in AD en ManagedBy in Exchange. Eigenaars van cloudgroepen komen enkel live.</summary>
    public IReadOnlyList<Relatie> BeheerdersVan(Groep g)
    {
        var uit = new List<Relatie>();
        if (g.Ad is { ManagedBy.Length: > 0 } ad)
        {
            uit.Add(new Relatie("AD", Ref(ad.ManagedBy)));
        }

        if (g.Exo is { } exo)
        {
            uit.AddRange(exo.ManagedBy.Select(m => new Relatie("Exchange", Ref(m))));
        }

        return uit;
    }

    public IReadOnlyList<RechtRegel> RechtenVan(ObjectRef wie) => _rechten.Where(r => r.Wie.IsZelfde(wie)).ToList();

    // ------------------------------------------------------------------ relaties van een postvak

    public IReadOnlyList<RechtRegel> RechtenOp(Postvak p)
        => _rechten.Where(r => r.Postvak == p).OrderBy(r => r.Wie.Naam, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Recht).ToList();

    /// <summary>De groep die toegang geeft: bestaande SG-SM-groep met rechten, anders de groep met de verwachte naam.</summary>
    public Groep? ToegangsgroepVan(Postvak p)
    {
        var metRechten = RechtenOp(p).Where(r => r.Wie.Soort == ObjectSoort.Groep).Select(r => Groep(r.Wie.Sleutel)).OfType<Groep>().ToList();
        var sgSm = metRechten.FirstOrDefault(g => g.Naam.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase));
        if (sgSm is not null)
        {
            return sgSm;
        }

        if (Zoek(VerwachteToegangsgroep(p)) is { Soort: ObjectSoort.Groep } verwacht)
        {
            return Groep(verwacht.Sleutel);
        }

        return metRechten.FirstOrDefault();
    }

    public static string VerwachteToegangsgroep(Postvak p) => $"SG-SM-{p.LokaalDeel}-Full";

    /// <summary>Groepen waarvan het postvak (of de room) lid is, in Exchange en in Entra.</summary>
    public IReadOnlyList<Relatie> GroepenVanAdres(string smtp)
    {
        var uit = new List<Relatie>();
        if (_exoOuders.TryGetValue(smtp, out var exo))
        {
            uit.AddRange(exo.Select(g => new Relatie("Exchange", g.Ref)));
        }

        var entra = _data.EntraGebruikers.FirstOrDefault(e => Cmp.Equals(e.UserPrincipalName, smtp) || Cmp.Equals(e.Mail, smtp));
        if (entra is not null && _data.CloudLidmaatschap.TryGetValue(entra.Id, out var ids))
        {
            uit.AddRange(ids.Select(id => Zoek(id)).OfType<ObjectRef>().Select(r => new Relatie("Entra", r)));
        }

        return Sorteer(uit.GroupBy(r => r.Object.Sleutel, Cmp).Select(g => g.First()).ToList());
    }

    // ------------------------------------------------------------------ relaties van een persoon

    /// <summary>Postvakrechten van een persoon: rechtstreeks en via de groepen waarvan hij lid is.</summary>
    public IReadOnlyList<RechtRegel> PostvakRechtenVan(Persoon p)
    {
        var ik = Ref(p);
        var groepen = GroepenVan(p).ToDictionary(g => g.Sleutel, Cmp);
        var uit = new List<RechtRegel>();
        foreach (var r in _rechten)
        {
            if (r.Wie.IsZelfde(ik))
            {
                uit.Add(r);
            }
            else if (r.Wie.Soort == ObjectSoort.Groep && groepen.TryGetValue(r.Wie.Sleutel, out var via))
            {
                uit.Add(r with { ViaGroep = via.Ref });
            }
        }

        return uit.OrderBy(r => r.Postvak.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Recht).ToList();
    }

    /// <summary>Alle groepen van een persoon (AD, Entra en Exchange op adres), rechtstreeks lid.</summary>
    public IEnumerable<Groep> GroepenVan(Persoon p)
    {
        var gezien = new HashSet<string>(Cmp);
        foreach (var g in _data.AdGroepenVan(p))
        {
            if (Zoek(g.DistinguishedName) is { Soort: ObjectSoort.Groep } r && gezien.Add(r.Sleutel) && Groep(r.Sleutel) is { } groep)
            {
                yield return groep;
            }
        }

        foreach (var g in _data.CloudGroepenVan(p))
        {
            if (GroepVan(g) is { } groep && gezien.Add(groep.Sleutel))
            {
                yield return groep;
            }
        }

        foreach (var adres in new[] { p.Upn, p.Entra?.Mail, p.Ad?.EmailAddress }.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(Cmp))
        {
            if (_exoOuders.TryGetValue(adres!, out var exo))
            {
                foreach (var g in exo.Where(g => gezien.Add(g.Sleutel)))
                {
                    yield return g;
                }
            }
        }
    }

    // ------------------------------------------------------------------ opbouw

    private void BouwGroepen()
    {
        foreach (var ad in _data.AdGroepenOpDn.Values)
        {
            var sleutel = "ad:" + (ad.SamAccountName.Length > 0 ? ad.SamAccountName : ad.DistinguishedName).ToLowerInvariant();
            _groepen.TryAdd(sleutel, new Groep { Sleutel = sleutel, Ad = ad });
        }

        var adOpSam = _groepen.Values.Where(g => g.Ad!.SamAccountName.Length > 0).ToDictionary(g => g.Ad!.SamAccountName, Cmp);
        foreach (var cloud in _data.CloudGroepen.Values)
        {
            if (cloud.UitAd && cloud.OnPremisesSamAccountName.Length > 0 && adOpSam.TryGetValue(cloud.OnPremisesSamAccountName, out var ad) && ad.Cloud is null)
            {
                ad.Cloud = cloud;
            }
            else
            {
                var sleutel = "cloud:" + cloud.Id.ToLowerInvariant();
                _groepen.TryAdd(sleutel, new Groep { Sleutel = sleutel, Cloud = cloud });
            }
        }

        var opMail = _groepen.Values.Where(g => g.Mail.Length > 0).GroupBy(g => g.Mail, Cmp).ToDictionary(g => g.Key, g => g.First(), Cmp);
        foreach (var exo in _data.ExoGroepen)
        {
            if (exo.Smtp.Length > 0 && opMail.TryGetValue(exo.Smtp, out var g) && g.Exo is null)
            {
                g.Exo = exo;
            }
            else
            {
                var sleutel = "exo:" + First(exo.Smtp, exo.Alias, exo.DisplayName).ToLowerInvariant();
                _groepen.TryAdd(sleutel, new Groep { Sleutel = sleutel, Exo = exo });
            }
        }
    }

    private void BouwZoektabel()
    {
        void Sterk(string? k, ObjectRef r)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                _sterk.TryAdd(k.Trim(), r);
            }
        }

        void Zwak(string? k, ObjectRef r)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                _zwak.TryAdd(k.Trim(), r);
            }
        }

        // Postvakken en rooms eerst: hun cloudaccount is ook een "gebruiker", maar de postvakpagina is nuttiger
        foreach (var p in _data.Postvakken.Where(p => p.Smtp.Length > 0))
        {
            _postvakken.TryAdd(p.Smtp, p);
            var r = new ObjectRef(ObjectSoort.Postvak, p.Smtp, First(p.DisplayName, p.Smtp), p.TypeTekst);
            Sterk(p.Smtp, r);
            Sterk(p.Alias, r);
            Zwak(p.DisplayName, r);
        }

        foreach (var room in _data.Rooms.Where(r => r.Smtp.Length > 0))
        {
            _rooms.TryAdd(room.Smtp, room);
            var r = new ObjectRef(ObjectSoort.Room, room.Smtp, First(room.DisplayName, room.Smtp), "room");
            Sterk(room.Smtp, r);
            Sterk(room.Alias, r);
            Zwak(room.DisplayName, r);
        }

        foreach (var e in _data.EntraGebruikers)
        {
            // Het Entra-account achter een postvak of room verwijst naar die pagina
            if (Zoek(e.UserPrincipalName) is { Soort: ObjectSoort.Postvak or ObjectSoort.Room } achter)
            {
                Sterk(e.Id, achter);
            }
            else if (Zoek(e.Mail) is { Soort: ObjectSoort.Postvak or ObjectSoort.Room } achter2)
            {
                Sterk(e.Id, achter2);
            }
        }

        foreach (var g in _groepen.Values)
        {
            var r = g.Ref;
            Sterk(g.Sleutel, r);
            Sterk(g.Ad?.DistinguishedName, r);
            Sterk(g.Ad?.SamAccountName, r);
            Sterk(g.Cloud?.Id, r);
            Sterk(g.Mail, r);
            Sterk(g.Exo?.Smtp, r);
            Sterk(g.Exo?.Alias, r);
            Sterk(g.Exo?.Name, r);
            Zwak(g.Naam, r);
            Zwak(g.Ad?.Name, r);
            Zwak(g.Exo?.DisplayName, r);
        }

        foreach (var p in _data.Personen)
        {
            var r = Ref(p);
            Sterk(p.Sleutel, r);
            Sterk(p.Ad?.DistinguishedName, r);
            Sterk(p.Ad?.SamAccountName, r);
            Sterk(p.Ad?.UserPrincipalName, r);
            Sterk(p.Ad?.EmailAddress, r);
            Sterk(p.Entra?.Id, r);
            Sterk(p.Entra?.UserPrincipalName, r);
            Sterk(p.Entra?.Mail, r);
            Zwak(p.Naam, r);
        }

        foreach (var (id, (soort, naam)) in _data.CloudLidNamen)
        {
            var objSoort = soort.Contains("device", StringComparison.OrdinalIgnoreCase) ? ObjectSoort.Apparaat
                : soort.Contains("contact", StringComparison.OrdinalIgnoreCase) ? ObjectSoort.Contact
                : ObjectSoort.Onbekend;
            Sterk(id, new ObjectRef(objSoort, string.Empty, First(naam, id), soort));
        }
    }

    private void BouwRelaties()
    {
        foreach (var (lid, groepIds) in _data.CloudLidmaatschap)
        {
            foreach (var g in groepIds)
            {
                if (!_cloudLeden.TryGetValue(g, out var lijst))
                {
                    _cloudLeden[g] = lijst = new List<string>();
                }

                lijst.Add(lid);
            }
        }

        foreach (var g in _groepen.Values)
        {
            if (g.Ad is { LedenBekend: true } ad)
            {
                foreach (var dn in ad.Leden)
                {
                    Voeg(_adOuders, dn, g);
                }
            }

            if (g.Exo is { } exo)
            {
                foreach (var l in exo.Leden)
                {
                    Voeg(_exoOuders, l.Smtp, g);
                }
            }
        }

        foreach (var r in _data.PostvakRechten)
        {
            if (_postvakken.TryGetValue(r.Postvak, out var p))
            {
                _rechten.Add(new RechtRegel(p, r.Recht, r.Trustee, Ref(r.Trustee)));
            }
        }
    }

    private ObjectRef CloudLid(string id) => Zoek(id) ?? ObjectRef.Onbekend(id, "onbekend lid");

    private static void Voeg(Dictionary<string, List<Groep>> d, string sleutel, Groep g)
    {
        if (string.IsNullOrWhiteSpace(sleutel))
        {
            return;
        }

        if (!d.TryGetValue(sleutel, out var lijst))
        {
            d[sleutel] = lijst = new List<Groep>();
        }

        if (!lijst.Contains(g))
        {
            lijst.Add(g);
        }
    }

    private static IReadOnlyList<Relatie> Sorteer(List<Relatie> lijst)
        => lijst.OrderBy(r => r.Laag == "AD" ? 0 : r.Laag == "Entra" ? 1 : 2)
            .ThenBy(r => r.Object.Soort)
            .ThenBy(r => r.Object.Naam, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string First(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
