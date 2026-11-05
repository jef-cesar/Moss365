using System.Net;
using System.Text;
using System.Text.Json;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Ad;
using Moss365.Connections.Exchange;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

public class AdLdapMapperTests
{
    private static Dictionary<string, object?[]> K(params (string Naam, object?[] Waarden)[] k)
        => k.ToDictionary(x => x.Naam, x => x.Waarden, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Gebruiker()
    {
        var aanmelding = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var g = AdLdapMapper.NaarGebruiker(K(
            ("sAMAccountName", new object?[] { "an.peeters" }),
            ("distinguishedName", new object?[] { "CN=An Peeters,OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=test,DC=internal" }),
            ("userAccountControl", new object?[] { 0x200 | 0x10000 }),
            ("lastLogonTimestamp", new object?[] { aanmelding.ToFileTimeUtc() }),
            ("physicalDeliveryOfficeName", new object?[] { "gemeentehuis" }),
            ("l", new object?[] { "intranet" }),
            ("memberOf", new object?[] { "CN=a,DC=x", "CN=b,DC=x" })));

        Assert.True(g.Enabled);
        Assert.True(g.PasswordNeverExpires);
        Assert.Equal("GEMEENTEBESTUUR/GEBRUIKERS", g.OuPad);
        Assert.Equal("gemeentehuis", g.Office);
        Assert.Equal("intranet", g.City);
        Assert.Equal(aanmelding.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), g.LastLogonDate);
        Assert.Equal(2, g.MemberOf.Count);
        Assert.Equal(string.Empty, g.Department);
    }

    [Fact]
    public void UitgeschakeldEnNooitAangemeld()
    {
        var g = AdLdapMapper.NaarGebruiker(K(("userAccountControl", new object?[] { 0x202 }), ("lastLogonTimestamp", new object?[] { 0L })));
        Assert.False(g.Enabled);
        Assert.Equal(string.Empty, g.LastLogonDate);
    }

    [Theory]
    [InlineData(unchecked((int)0x80000002), "Security", "Global")]
    [InlineData(unchecked((int)0x80000004), "Security", "DomainLocal")]
    [InlineData(0x8, "Distribution", "Universal")]
    public void GroepSoort(int groupType, string categorie, string scope)
    {
        var g = AdLdapMapper.NaarGroep(K(("groupType", new object?[] { groupType }), ("member", new object?[] { "a", "b", "c" })));
        Assert.Equal(categorie, g.Category);
        Assert.Equal(scope, g.Scope);
        Assert.Equal(3, g.AantalLeden);
    }

    [Fact]
    public void PadEnDomein()
    {
        Assert.Equal("DC=CONTOSO,DC=LOCAL", AdLdapMapper.DomeinNaarDn("CONTOSO.LOCAL"));
        Assert.Equal("LDAP://10.0.0.1/CN=a\\/b,DC=x", AdLdapMapper.LdapPad("10.0.0.1", "CN=a/b,DC=x"));
        Assert.Equal("LDAP://DC=x", AdLdapMapper.LdapPad("", "DC=x"));
    }
}

/// <summary>Nagebootste Graph: antwoorden per methode en pad, en een lijst van alle verzoeken.</summary>
internal sealed class NepGraph : HttpMessageHandler
{
    private readonly List<(HttpMethod Methode, string Pad, Func<HttpRequestMessage, HttpResponseMessage> Antwoord)> _regels = new();

    public List<(HttpMethod Methode, string Url, string? Inhoud)> Verzoeken { get; } = new();

    public NepGraph Op(HttpMethod methode, string padBegin, object antwoord, HttpStatusCode status = HttpStatusCode.OK)
    {
        _regels.Add((methode, padBegin, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(antwoord as string ?? JsonSerializer.Serialize(antwoord), Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage verzoek, CancellationToken annuleren)
    {
        var url = verzoek.RequestUri!.ToString();
        Verzoeken.Add((verzoek.Method, url, verzoek.Content is null ? null : await verzoek.Content.ReadAsStringAsync(annuleren)));
        var pad = url.Replace(GraphKlant.Basis, string.Empty);
        var regel = _regels.LastOrDefault(r => r.Methode == verzoek.Method && pad.StartsWith(r.Pad, StringComparison.Ordinal));
        return regel.Antwoord?.Invoke(verzoek) ?? new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"niet gevonden: " + pad + "\"}}"),
        };
    }
}

public class GraphVerbindingTests
{
    private static GraphVerbinding Maak(NepGraph nep) => new(new GraphKlant(new HttpClient(nep), _ => Task.FromResult("token")));

    private static object Lijst(params object[] items) => new { value = items };

    [Fact]
    public async Task LeestGebruikersGroepenLedenEnLicentiesMetPaginering()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Lijst(new { skuId = "s1", skuPartNumber = "SPB", consumedUnits = 163, prepaidUnits = new { enabled = 163 } }))
            .Op(HttpMethod.Get, "/users?", new Dictionary<string, object>
            {
                ["value"] = new object[]
                {
                    new
                    {
                        id = "u1", userPrincipalName = "an@test.be", displayName = "An", accountEnabled = true, userType = "Member",
                        onPremisesSyncEnabled = true, onPremisesSamAccountName = "an.peeters", city = "intranet",
                        assignedLicenses = new[] { new { skuId = "s1" } },
                        signInActivity = new { lastSignInDateTime = "2026-10-01T08:00:00Z", lastNonInteractiveSignInDateTime = "2026-10-10T08:00:00Z" },
                    },
                },
                ["@odata.nextLink"] = GraphKlant.Basis + "/users-pagina2",
            })
            .Op(HttpMethod.Get, "/users-pagina2", Lijst(new { id = "u2", userPrincipalName = "gast#EXT#@test.onmicrosoft.com", userType = "Guest" }))
            .Op(HttpMethod.Get, "/groups?", Lijst(
                new { id = "g1", displayName = "SG-SM-info-Full", mailEnabled = true, securityEnabled = true, groupTypes = Array.Empty<string>(), mail = "sg@test.be" },
                new { id = "g2", displayName = "Alle gebruikers", mailEnabled = false, securityEnabled = true, groupTypes = new[] { "DynamicMembership" }, membershipRule = "All Users" }))
            .Op(HttpMethod.Get, "/groups/g1/members", Lijst(new { id = "u1" }))
            .Op(HttpMethod.Get, "/groups/g2/members", Lijst(new { id = "u1" }, new { id = "u2" }));

        var d = await Maak(nep).LeesAsync(null, CancellationToken.None);

        Assert.Equal(2, d.Gebruikers.Count);
        var an = d.Gebruikers[0];
        Assert.Equal(new[] { "SPB" }, an.Licenties);
        Assert.Equal("intranet", an.City);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), an.LaatsteAanmelding);
        Assert.True(d.Gebruikers[1].IsGast);

        Assert.Equal(Moss365.Core.Inventaris.CloudGroepType.MailBeveiliging, d.Groepen[0].Type);
        Assert.Equal("sg@test.be", d.Groepen[0].Mail);
        Assert.True(d.Groepen[1].Dynamisch);
        Assert.Equal(2, d.Groepen[1].AantalLeden);
        Assert.Equal(new[] { "g1", "g2" }, d.Lidmaatschap["u1"].OrderBy(x => x));
        Assert.Equal(163, Assert.Single(d.Licenties).Totaal);
        Assert.All(nep.Verzoeken, v => Assert.Equal(HttpMethod.Get, v.Methode));
    }

    [Fact]
    public async Task ZonderAanmeldgegevensVerderZonderSignInActivity()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Lijst())
            .Op(HttpMethod.Get, "/users?", Lijst(new { id = "u1", userPrincipalName = "an@test.be" }))
            .Op(HttpMethod.Get, "/users?$select=id,userPrincipalName,displayName,givenName,surname,mail,mailNickname,accountEnabled,userType,department,jobTitle,officeLocation,companyName,city,usageLocation,createdDateTime,onPremisesSyncEnabled,onPremisesSamAccountName,onPremisesDistinguishedName,assignedLicenses,signInActivity",
                "{\"error\":{\"code\":\"Authentication_RequestFromNonPremiumTenantOrB2CTenant\",\"message\":\"geen P1\"}}", HttpStatusCode.Forbidden)
            .Op(HttpMethod.Get, "/groups?", Lijst());

        var d = await Maak(nep).LeesAsync(null, CancellationToken.None);
        Assert.Single(d.Gebruikers);
        Assert.Equal(2, nep.Verzoeken.Count(v => v.Url.Contains("/users?")));
    }

    [Fact]
    public async Task GroepLidToevoegenMetUpnOpzoeken()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/users/eva%40test.be", new { id = "u-eva" })
            .Op(HttpMethod.Post, "/groups/g1/members/$ref", "", HttpStatusCode.NoContent);
        var w = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphGroepLidToevoegen, Doel = "eva@test.be", Omschrijving = "x" };
        w.Parameters["groepId"] = "g1";

        var r = Assert.Single(await Maak(nep).VoerUitAsync(new[] { w }, CancellationToken.None));

        Assert.Equal(ResultaatStatus.Gelukt, r.Status);
        var post = nep.Verzoeken.Single(v => v.Methode == HttpMethod.Post);
        Assert.EndsWith("/groups/g1/members/$ref", post.Url);
        Assert.Contains("directoryObjects/u-eva", post.Inhoud);
    }

    [Fact]
    public async Task AlLidIsGeenFout()
    {
        var nep = new NepGraph().Op(HttpMethod.Post, "/groups/g1/members/$ref",
            "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"One or more added object references already exist for the following modified properties: 'members'.\"}}",
            HttpStatusCode.BadRequest);
        var w = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphGroepLidToevoegen, Doel = "u1", Omschrijving = "x" };
        w.Parameters["groepId"] = "g1";
        var r = Assert.Single(await Maak(nep).VoerUitAsync(new[] { w }, CancellationToken.None));
        Assert.Equal(ResultaatStatus.Gelukt, r.Status);
        Assert.Equal("Was al lid.", r.Melding);
    }

    [Fact]
    public async Task LicentieOpNaamEnOnbekendeGebruiker()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Lijst(new { skuId = "s-f3", skuPartNumber = "DESKLESSPACK" }))
            .Op(HttpMethod.Post, "/users/u1/assignLicense", "{}");
        var lic = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphLicentieToekennen, Doel = "u1", Omschrijving = "x" };
        lic.Parameters["sku"] = "DESKLESSPACK";
        var onbekend = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphSessiesIntrekken, Doel = "weg@test.be", Omschrijving = "x" };

        var r = await Maak(nep).VoerUitAsync(new[] { lic, onbekend }, CancellationToken.None);

        Assert.Equal(ResultaatStatus.Gelukt, r[0].Status);
        Assert.Contains("\"skuId\":\"s-f3\"", nep.Verzoeken.Single(v => v.Methode == HttpMethod.Post).Inhoud);
        Assert.Equal(ResultaatStatus.Mislukt, r[1].Status);
        Assert.Contains("bestaat (nog) niet", r[1].Melding);
    }

    [Fact]
    public void KenmerkenWordenJuistGetypeerd()
    {
        var inhoud = GraphVerbinding.KenmerkenInhoud(new Dictionary<string, string> { ["accountEnabled"] = "false", ["usageLocation"] = "BE", ["jobTitle"] = "" });
        Assert.Equal(false, inhoud["accountEnabled"]);
        Assert.Equal("BE", inhoud["usageLocation"]);
        Assert.Null(inhoud["jobTitle"]);
    }
}

public class ExchangeVerbindingTests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-exo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    /// <summary>Doet alsof PowerShell draait: leest de opdrachten en schrijft resultaten via een functie.</summary>
    private sealed class NepPowerShell : IPowerShellStarter
    {
        private readonly Func<JsonElement, object> _antwoord;

        public NepPowerShell(Func<JsonElement, object> antwoord) => _antwoord = antwoord;

        public List<IReadOnlyList<string>> Aanroepen { get; } = new();
        public List<IReadOnlyDictionary<string, string>> Omgevingen { get; } = new();
        public string? ScriptInhoud { get; private set; }

        public Task<(int Code, string Fouten)> StartAsync(string script, IReadOnlyList<string> argumenten, IReadOnlyDictionary<string, string>? omgeving, CancellationToken annuleren)
        {
            Aanroepen.Add(argumenten);
            Omgevingen.Add(omgeving ?? new Dictionary<string, string>());
            ScriptInhoud = File.ReadAllText(script);
            var invoer = argumenten[argumenten.ToList().IndexOf("-Opdrachten") + 1];
            var uitvoer = argumenten[argumenten.ToList().IndexOf("-Uitvoer") + 1];
            using var doc = JsonDocument.Parse(File.ReadAllText(invoer));
            var resultaten = doc.RootElement.EnumerateArray().Select(_antwoord).ToList();
            File.WriteAllText(uitvoer, JsonSerializer.Serialize(resultaten));
            return Task.FromResult((0, string.Empty));
        }
    }

    [Fact]
    public void WijzigingenWordenOpdrachten()
    {
        var lid = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeGroepLidToevoegen, Doel = "eva@test.be", Omschrijving = "x" };
        lid.Parameters["groep"] = "sg@test.be";
        var o = ExchangeVerbinding.NaarOpdracht(lid);
        Assert.Equal("LidToevoegen", o.Op);
        Assert.Equal("sg@test.be", o.Params["Identity"]);
        Assert.Equal("eva@test.be", o.Params["Member"]);

        var room = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeRoomAgendaverwerking, Doel = "rm@test.be", Omschrijving = "x" };
        room.Parameters["BookingWindowInDays"] = "180";
        var r = ExchangeVerbinding.NaarOpdracht(room);
        Assert.Equal("Agendaverwerking", r.Op);
        Assert.Equal("rm@test.be", r.Params["Identity"]);
        Assert.Equal("180", r.Params["BookingWindowInDays"]);

        Assert.Throws<InvalidOperationException>(() => ExchangeVerbinding.NaarOpdracht(
            new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.AdAccountUitschakelen, Doel = "x", Omschrijving = "x" }));
    }

    [Fact]
    public async Task VoertUitInEenProcesEnLeestDeResultaten()
    {
        var nep = new NepPowerShell(o => new
        {
            id = o.GetProperty("id").GetString(),
            status = o.GetProperty("params").GetProperty("Member").GetString() == "fout@test.be" ? "Mislukt" : "Gelukt",
            melding = "",
            data = (object?)null,
        });
        var verbinding = new ExchangeVerbinding("beheer@test.be", nep, _map);
        var w1 = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeGroepLidToevoegen, Doel = "eva@test.be", Omschrijving = "x" };
        var w2 = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeGroepLidToevoegen, Doel = "fout@test.be", Omschrijving = "x" };

        var r = await verbinding.VoerUitAsync(new[] { w1, w2 }, CancellationToken.None);

        Assert.Single(nep.Aanroepen);
        Assert.Contains("beheer@test.be", nep.Aanroepen[0]);
        Assert.Contains("$Toegestaan", nep.ScriptInhoud);
        Assert.Equal(ResultaatStatus.Gelukt, r[0].Status);
        Assert.Equal(ResultaatStatus.Mislukt, r[1].Status);
        Assert.Empty(Directory.GetFiles(_map, "*.json"));
    }

    [Fact]
    public async Task TokenGaatViaDeOmgevingNietViaDeOpdrachtregel()
    {
        var nep = new NepPowerShell(o => new { id = o.GetProperty("id").GetString(), status = "Gelukt", melding = "", data = (object?)null });
        var verbinding = new ExchangeVerbinding(null, nep, _map, token: _ => Task.FromResult<string?>("geheim-token"), aangemeldAls: () => "beheer@test.be");
        var w = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeGroepLidToevoegen, Doel = "eva@test.be", Omschrijving = "x" };

        await verbinding.VoerUitAsync(new[] { w }, CancellationToken.None);

        Assert.Equal("geheim-token", nep.Omgevingen[0][ExchangeVerbinding.TokenVariabele]);
        Assert.DoesNotContain(nep.Aanroepen[0], a => a.Contains("geheim-token"));
        Assert.Contains("beheer@test.be", nep.Aanroepen[0]);
        Assert.Empty(Directory.GetFiles(_map, "*.json"));

        // Zonder token: geen omgevingsvariabele
        var zonder = new NepPowerShell(o => new { id = o.GetProperty("id").GetString(), status = "Gelukt", melding = "", data = (object?)null });
        await new ExchangeVerbinding(null, zonder, _map, token: _ => Task.FromResult<string?>(null)).VoerUitAsync(new[] { w }, CancellationToken.None);
        Assert.False(zonder.Omgevingen[0].ContainsKey(ExchangeVerbinding.TokenVariabele));
    }

    [Fact]
    public void NieuweWijzigingenWordenOpdrachten()
    {
        Wijziging W(Actie a, string doel, params (string, string)[] p)
        {
            var w = new Wijziging { Systeem = Systeem.Exchange, Actie = a, Doel = doel, Omschrijving = "x" };
            foreach (var (k, v) in p) w.Parameters[k] = v;
            return w;
        }

        var recht = ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangePostvakRechtVerwijderen, "info@test.be", ("Recht", "SendAs"), ("Trustee", "bo@test.be")));
        Assert.Equal(("RechtVerwijderen", "info@test.be", "SendAs", "bo@test.be"), (recht.Op, recht.Params["Identity"], recht.Params["Recht"], recht.Params["Trustee"]));

        var instellen = ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangePostvakInstellen, "info@test.be", ("ForwardingSmtpAddress", ""), ("onbekend", "x")));
        Assert.Equal("PostvakInstellen", instellen.Op);
        Assert.Equal("", instellen.Params["ForwardingSmtpAddress"]);
        Assert.False(instellen.Params.ContainsKey("onbekend"));

        var nieuw = ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangeGedeeldPostvakAanmaken, "jeugd@test.be", ("Name", "Jeugd"), ("DisplayName", "Jeugd"), ("Alias", "jeugd")));
        Assert.Equal(("PostvakAanmaken", "jeugd@test.be"), (nieuw.Op, nieuw.Params["PrimarySmtpAddress"]));

        Assert.Equal("GroepAanmaken", ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangeGroepAanmaken, "sg@test.be", ("Name", "SG"))).Op);
        Assert.Equal("Beheerder", ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangeGroepBeheerder, "sg@test.be", ("Toevoegen", "an@test.be"))).Op);
        Assert.Equal("GroepInstellen", ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangeGroepInstellen, "sg@test.be", ("DisplayName", "SG"))).Op);
        Assert.Equal("AutoAntwoord", ExchangeVerbinding.NaarOpdracht(W(Actie.ExchangeAutoAntwoord, "info@test.be", ("AutoReplyState", "Disabled"))).Op);
    }

    [Fact]
    public async Task PostvakkenLezen()
    {
        var nep = new NepPowerShell(o => new
        {
            id = "postvakken",
            status = "Gelukt",
            melding = "",
            data = new
            {
                Postvakken = new[] { new { Type = "SharedMailbox", DisplayName = "Info", Smtp = "info@test.be", Hidden = "True", DoorsturenNaar = "x@fabrikam.com", DoorsturenEnBewaren = "True", AutoAntwoord = "Disabled" } },
                Rechten = new[] { new { Postvak = "info@test.be", Recht = "FullAccess,SendAs", Trustee = "sg@test.be" } },
                Groepen = new[] { new { Type = "MailUniversalSecurityGroup", DisplayName = "SG", Smtp = "sg@test.be", ManagedBy = "an|bo", Leden = new[] { new { Smtp = "an@test.be", Naam = "An" } } } },
            },
        });
        var g = await new ExchangeVerbinding(null, nep, _map).LeesPostvakkenAsync(null, CancellationToken.None);
        var p = Assert.Single(g.Postvakken);
        Assert.True(p.Verborgen);
        Assert.Equal("x@fabrikam.com", p.DoorsturenNaar);
        Assert.True(p.DoorsturenEnBewaren);
        Assert.Equal("Disabled", p.AutoAntwoord);
        Assert.Equal(new[] { "FullAccess", "SendAs" }, g.Rechten.Select(r => r.Recht));
        var groep = Assert.Single(g.Groepen);
        Assert.Equal(new[] { "an", "bo" }, groep.ManagedBy);
        Assert.Equal(1, groep.AantalLeden);
        Assert.Equal("an@test.be", Assert.Single(groep.Leden).Smtp);
    }

    [Fact]
    public async Task RoomsLezen()
    {
        var nep = new NepPowerShell(o => new
        {
            id = "rooms",
            status = "Gelukt",
            melding = "",
            data = new[] { new { Smtp = "rm@test.be", DisplayName = "Zaal", Capaciteit = 8, BookingWindowInDays = "180", AllowConflicts = "False", AgendaRechten = "Default=Reviewer" } },
        });
        var rooms = await new ExchangeVerbinding(null, nep, _map).LeesRoomsAsync(null, CancellationToken.None);
        var room = Assert.Single(rooms);
        Assert.Equal(8, room.Capaciteit);
        Assert.Equal(180, room.BookingWindowInDays);
        Assert.Equal("Reviewer", room.StandaardAgendaRecht);
    }

    [Fact]
    public async Task ZonderResultaatbestandEenDuidelijkeFout()
    {
        var verbinding = new ExchangeVerbinding(null, new LegePowerShell(), _map);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => verbinding.LeesRoomsAsync(null, CancellationToken.None));
        Assert.Contains("module ontbreekt", ex.Message);
    }

    private sealed class LegePowerShell : IPowerShellStarter
    {
        public Task<(int Code, string Fouten)> StartAsync(string script, IReadOnlyList<string> argumenten, IReadOnlyDictionary<string, string>? omgeving, CancellationToken annuleren)
            => Task.FromResult((1, "module ontbreekt"));
    }

    [Fact]
    public void EnkelResultaatZonderLijst()
    {
        var r = Assert.Single(ExchangeVerbinding.LeesResultaten("﻿{\"id\":\"a\",\"status\":\"Gelukt\",\"melding\":\"\",\"data\":null}"));
        Assert.Equal("a", r.Id);
        Assert.Null(r.Data);
    }
}

public class LiveLaderTests
{
    private sealed class NepAd : IAdVerbinding
    {
        public Task<AdGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren)
            => Task.FromResult(new AdGegevens(
                new[] { new Moss365.Core.Inventaris.AdGebruiker { SamAccountName = "live.gebruiker", DistinguishedName = "CN=Live,DC=x", Enabled = true } },
                Array.Empty<Moss365.Core.Inventaris.AdGroep>(),
                "DC=LIVE,DC=INTERNAL"));

        public Task<Moss365.Core.Inventaris.AdGebruiker?> LeesGebruikerAsync(string samAccountName, CancellationToken annuleren)
            => Task.FromResult<Moss365.Core.Inventaris.AdGebruiker?>(null);

        public Task<Moss365.Core.Inventaris.AdGroep?> LeesGroepAsync(string samAccountName, CancellationToken annuleren)
            => Task.FromResult<Moss365.Core.Inventaris.AdGroep?>(null);
    }

    [Fact]
    public async Task VervangtAdEnHoudtDeRest()
    {
        var huidig = TestData.Omgeving();
        var live = await LiveLader.LaadAsync(new NepAd(), null, huidig, null, CancellationToken.None);

        Assert.Equal("Live", live.Herkomst);
        Assert.Equal("DC=LIVE,DC=INTERNAL", live.DomeinDn);
        Assert.Single(live.AdGebruikers);
        Assert.Equal(huidig.EntraGebruikers.Count, live.EntraGebruikers.Count);
        Assert.Equal(huidig.Rooms.Count, live.Rooms.Count);
        Assert.Contains(live.Bronnen, b => b.Map!.StartsWith("live"));

        var metRooms = LiveLader.MetRooms(live, Array.Empty<Moss365.Core.Inventaris.Room>());
        Assert.Empty(metRooms.Rooms);
        Assert.Single(metRooms.AdGebruikers);
    }
}
