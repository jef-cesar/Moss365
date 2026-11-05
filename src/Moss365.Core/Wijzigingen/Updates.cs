namespace Moss365.Core.Wijzigingen;

/// <summary>Uitkomst van het zoeken naar een nieuwe versie.</summary>
/// <param name="NieuwsteVersie">De versie van de laatste release (zonder "v"), of null als die niet op te vragen was.</param>
/// <param name="Link">De pagina van de release (of van alle releases).</param>
/// <param name="Melding">Waarom er geen antwoord is (bv. een privé-repo), anders null.</param>
public sealed record UpdateInfo(string? NieuwsteVersie, string Link, string? Melding)
{
    public bool IsNieuwer(string huidig) => NieuwsteVersie is not null && Versies.IsNieuwer(NieuwsteVersie, huidig);
}

public interface IUpdateControle
{
    /// <summary>Waar alle releases staan (om te kopiëren of te openen).</summary>
    string ReleasesLink { get; }

    Task<UpdateInfo> ControleerAsync(CancellationToken annuleren);
}

public static class Versies
{
    /// <summary>"v0.5.1" tegenover "0.5.0": true. Onleesbare versies zijn nooit nieuwer.</summary>
    public static bool IsNieuwer(string kandidaat, string huidig)
        => Lees(kandidaat) is { } k && Lees(huidig) is { } h && k > h;

    /// <summary>Leest "v1.2.3", "1.2" of "1.2.3+abc" (de buildtoevoeging telt niet mee).</summary>
    public static Version? Lees(string? tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst))
        {
            return null;
        }

        var t = tekst.Trim().TrimStart('v', 'V');
        var einde = t.IndexOfAny(new[] { '+', '-', ' ' });
        if (einde >= 0)
        {
            t = t[..einde];
        }

        if (!Version.TryParse(t, out var v))
        {
            return null;
        }

        // Ontbrekende delen tellen als 0, zodat 0.5 en 0.5.0 gelijk zijn
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
