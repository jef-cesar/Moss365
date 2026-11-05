namespace Moss365.Core.Inventaris;

/// <summary>MFA-registratie van één Entra-gebruiker (rapport userRegistrationDetails).</summary>
public sealed record MfaRegistratie(
    string GebruikerId,
    string Upn,
    bool MfaGeregistreerd,
    bool MfaMogelijk,
    IReadOnlyList<string> Methodes,
    string StandaardMethode,
    bool IsBeheerder);

/// <summary>Eén lid van een (actieve) Entra-beheerrol. RoleTemplateId is dezelfde id als in Conditional Access.</summary>
public sealed record RolLid(string Rol, string LidId, string LidNaam, string LidUpn, string LidSoort, string RoleTemplateId = "");

/// <summary>
/// Beveiligingsgegevens uit Entra. Een melding betekent dat dat deel niet gelezen kon worden (rechten, licentie); de
/// lijst is dan leeg en de app toont "onbekend", geen "in orde".
/// </summary>
public sealed record BeveiligingGegevens(
    IReadOnlyList<MfaRegistratie> Mfa,
    IReadOnlyList<RolLid> Rollen,
    string? MfaMelding = null,
    string? RollenMelding = null)
{
    public bool MfaBekend => MfaMelding is null;
    public bool RollenBekend => RollenMelding is null;

    public MfaRegistratie? MfaVan(string? entraId)
        => entraId is null ? null : Mfa.FirstOrDefault(m => string.Equals(m.GebruikerId, entraId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<RolLid> RollenVan(string? entraId)
        => entraId is null ? Enumerable.Empty<RolLid>() : Rollen.Where(r => string.Equals(r.LidId, entraId, StringComparison.OrdinalIgnoreCase));

    /// <summary>De rollen die over de hele tenant gaan; wie die heeft, verdient extra aandacht.</summary>
    public static bool IsKritiekeRol(string rol) => rol is "Global Administrator" or "Privileged Role Administrator" or "Security Administrator"
        or "Exchange Administrator" or "SharePoint Administrator" or "User Administrator" or "Privileged Authentication Administrator"
        or "Conditional Access Administrator" or "Application Administrator" or "Cloud Application Administrator" or "Hybrid Identity Administrator";
}

/// <summary>Eén aanmelding (auditLogs/signIns).</summary>
public sealed record Aanmelding(
    DateTimeOffset Tijd,
    string GebruikerId,
    string Upn,
    string Naam,
    string App,
    string Ip,
    string Locatie,
    int Foutcode,
    string Reden,
    string Client,
    IReadOnlyList<CaToepassing>? Ca = null)
{
    public bool Gelukt => Foutcode == 0;
}
