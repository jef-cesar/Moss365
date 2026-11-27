using System.Buffers.Binary;
using System.Text;
using Moss365.Connections.Dns;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Mail;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>G0, U31: where the mail lives, by tenant, by person and by the MX records of each domain.</summary>
public class MailLocationTests
{
    private sealed class FakeMx(Dictionary<string, string[]> answers) : IMxLookup
    {
        public Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken cancel = default)
            => answers.TryGetValue(domain, out var hosts)
                ? Task.FromResult<IReadOnlyList<MxRecord>>(hosts.Select((h, i) => new MxRecord(10 * (hosts.Length - i), h)).ToList())
                : throw new TimeoutException("no answer");
    }

    private static Person P(string key, string mail) => new() { Key = key, Entra = new EntraUser { Id = key, Mail = mail, UserPrincipalName = mail, DisplayName = key } };

    [Theory]
    [InlineData("contoso-com.mail.protection.outlook.com", MailSystem.ExchangeOnline)]
    [InlineData("ASPMX.L.GOOGLE.COM.", MailSystem.Gmail)]
    [InlineData("alt1.aspmx.l.google.com", MailSystem.Gmail)]
    [InlineData("smtp.google.com", MailSystem.Gmail)]
    [InlineData("mail.contoso.com", MailSystem.Other)]
    [InlineData("eu-smtp-inbound-1.mimecast.com", MailSystem.Other)]
    public void AnMxHostBelongsToItsSystem(string host, MailSystem expected) => Assert.Equal(expected, MailLocations.Classify(new[] { host }));

    [Fact]
    public void NoMxIsNoMailAndThePreferredRecordDecides()
    {
        Assert.Equal(MailSystem.None, MailLocations.Classify(Array.Empty<string>()));
        Assert.Equal(MailSystem.ExchangeOnline, MailLocations.Classify(new[] { "contoso-com.mail.protection.outlook.com", "aspmx.l.google.com" }));
    }

    [Fact]
    public void TheDomainsComeFromTheAddressesWithTheMostFirst()
    {
        var data = new InventoryData
        {
            Persons = new[] { P("a", "a@contoso.com"), P("b", "b@contoso.com"), P("c", "c@fabrikam.com"), P("d", "d@contoso.onmicrosoft.com"), new Person { Key = "e", Entra = new EntraUser { Id = "e", Mail = "x@guest.example", UserType = "Guest" } } },
        };

        Assert.Equal(new[] { "contoso.com", "fabrikam.com" }, MailLocations.Domains(data, null));
        Assert.Equal(new[] { "example.com", "contoso.com", "fabrikam.com" }, MailLocations.Domains(data, "Example.com"));
        Assert.False(MailLocations.IsDomain("bad domain.com"));
        Assert.False(MailLocations.IsDomain("contoso..com"));
    }

    [Fact]
    public void TheDefaultIsProposedFromTheMainDomainOrTheSystems()
    {
        var gmail = new[] { new MailRoute("contoso.com", new[] { "aspmx.l.google.com" }, MailSystem.Gmail) };
        Assert.Equal(MailSystem.Gmail, MailLocations.ProposeDefault(gmail, TenantSystems.Microsoft365 | TenantSystems.GoogleWorkspace));
        Assert.Equal(MailSystem.ExchangeOnline, MailLocations.ProposeDefault(Array.Empty<MailRoute>(), TenantSystems.Both));
        Assert.Equal(MailSystem.Gmail, MailLocations.ProposeDefault(Array.Empty<MailRoute>(), TenantSystems.GoogleWorkspace));
        Assert.Equal(MailSystem.ExchangeOnPremises, MailLocations.ProposeDefault(Array.Empty<MailRoute>(), TenantSystems.ActiveDirectory));
        // Gmail for a tenant without Google Workspace is not proposed: the systems decide
        Assert.Equal(MailSystem.ExchangeOnline, MailLocations.ProposeDefault(gmail, TenantSystems.Microsoft365));
        Assert.DoesNotContain(MailSystem.Gmail, MailLocations.Choices(TenantSystems.Both));
        Assert.Contains(MailSystem.Gmail, MailLocations.Choices(TenantSystems.GoogleWorkspace));
    }

    [Fact]
    public void APersonFollowsTheHandThenTheDomainThenTheDefault()
    {
        var settings = new MailLocationSettings
        {
            Default = MailSystem.ExchangeOnline,
            Routes = new[]
            {
                new MailRoute("contoso.com", new[] { "contoso-com.mail.protection.outlook.com" }, MailSystem.ExchangeOnline),
                new MailRoute("fabrikam.com", new[] { "aspmx.l.google.com" }, MailSystem.Gmail),
            },
            Users = new Dictionary<string, MailSystem> { ["b"] = MailSystem.Gmail },
        };
        var systems = TenantSystems.Both | TenantSystems.GoogleWorkspace;

        Assert.Equal((MailSystem.ExchangeOnline, MailLocationReason.Default), MailLocations.Of(P("a", "a@contoso.com"), settings, systems));
        Assert.Equal((MailSystem.Gmail, MailLocationReason.ByHand), MailLocations.Of(P("b", "b@contoso.com"), settings, systems));
        Assert.Equal((MailSystem.Gmail, MailLocationReason.Domain), MailLocations.Of(P("c", "c@fabrikam.com"), settings, systems));
        Assert.Equal("fabrikam.com", Assert.Single(MailLocations.Differing(settings, systems)).Domain);
    }

    [Fact]
    public async Task ADomainWithoutAnswerKeepsItsErrorAndTheRestGoesOn()
    {
        var dns = new FakeMx(new Dictionary<string, string[]> { ["contoso.com"] = new[] { "mx2.contoso.com", "contoso-com.mail.protection.outlook.com" } });

        var routes = await MailLocations.CheckAsync(dns, new[] { "contoso.com", "fabrikam.com", "not a domain" });

        Assert.Equal(2, routes.Count);
        Assert.Equal(MailSystem.ExchangeOnline, routes[0].System); // the preferred record (lowest number) decides
        Assert.Equal("contoso-com.mail.protection.outlook.com", routes[0].Hosts[0]);
        Assert.Equal(MailSystem.Unknown, routes[1].System);
        Assert.Equal("no answer", routes[1].Error);
    }

    [Fact]
    public void TheDnsQuestionAsksForMxOfTheDomain()
    {
        var q = MxLookup.Query("contoso.com", 0x1234);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(q));
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(2)));
        Assert.Equal(new byte[] { 7, (byte)'c', (byte)'o', (byte)'n', (byte)'t', (byte)'o', (byte)'s', (byte)'o', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 15, 0, 1 }, q.Skip(12).ToArray());
    }

    [Fact]
    public void TheDnsAnswerIsReadWithCompressedNames()
    {
        // contoso.com MX 10 contoso-com.mail.protection.outlook.com and MX 20 backup.contoso.com (pointer to the question)
        var m = new List<byte>();
        void U16(int v) { m.Add((byte)(v >> 8)); m.Add((byte)v); }
        void Name(params string[] labels) { foreach (var l in labels) { m.Add((byte)l.Length); m.AddRange(Encoding.ASCII.GetBytes(l)); } }
        U16(0x4321); U16(0x8180); U16(1); U16(2); U16(0); U16(0);
        Name("contoso", "com"); m.Add(0); U16(15); U16(1);
        // answer 1: name = pointer to offset 12
        U16(0xC00C); U16(15); U16(1); U16(0); U16(300);
        var rdata1 = new List<byte> { 0, 10 };
        var target = new List<byte>();
        foreach (var l in new[] { "contoso-com", "mail", "protection", "outlook", "com" }) { target.Add((byte)l.Length); target.AddRange(Encoding.ASCII.GetBytes(l)); }
        target.Add(0);
        rdata1.AddRange(target);
        U16(rdata1.Count); m.AddRange(rdata1);
        // answer 2: backup + pointer to "contoso.com"
        U16(0xC00C); U16(15); U16(1); U16(0); U16(300);
        var rdata2 = new List<byte> { 0, 20, 6 };
        rdata2.AddRange(Encoding.ASCII.GetBytes("backup"));
        rdata2.Add(0xC0); rdata2.Add(0x0C);
        U16(rdata2.Count); m.AddRange(rdata2);

        var records = MxLookup.Parse(m.ToArray(), 0x4321);

        Assert.Equal(2, records.Count);
        Assert.Equal(new MxRecord(10, "contoso-com.mail.protection.outlook.com"), records[0]);
        Assert.Equal(new MxRecord(20, "backup.contoso.com"), records[1]);
    }

    [Fact]
    public void ADomainThatDoesNotExistHasNoMxAndABadAnswerIsRefused()
    {
        var nx = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(nx, 7);
        BinaryPrimitives.WriteUInt16BigEndian(nx.AsSpan(2), 0x8183);
        Assert.Empty(MxLookup.Parse(nx, 7));

        Assert.Throws<InvalidDataException>(() => MxLookup.Parse(nx, 8));
        var loop = new byte[] { 0, 9, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12 };
        Assert.Throws<InvalidDataException>(() => MxLookup.Parse(loop, 9));
    }
}
