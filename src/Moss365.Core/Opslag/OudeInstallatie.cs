namespace Moss365.Core.Opslag;

/// <summary>
/// Overzetten van een installatie van de interne voorganger (0.9). Die stond in een andere map onder %LOCALAPPDATA%; de
/// app herkent ze aan haar inhoud (de databank en de organisatie-instellingen), niet aan haar naam. Er wordt enkel
/// gekopieerd: de oude map blijft staan tot de beheerder ze zelf weghaalt.
/// </summary>
public static class OudeInstallatie
{
    /// <summary>Het bestand in de nieuwe gegevensmap dat zegt waar de gegevens vandaan kwamen.</summary>
    public const string Merkteken = "imported-from.txt";

    /// <summary>Een map naast de nieuwe gegevensmap met een databank en organisatie-instellingen, of null.</summary>
    public static string? Zoek(string basis, string nieuweMap)
    {
        if (!Directory.Exists(basis))
        {
            return null;
        }

        var nieuw = Path.GetFullPath(nieuweMap).TrimEnd(Path.DirectorySeparatorChar);
        return Directory.EnumerateDirectories(basis)
            .Where(m => !string.Equals(Path.GetFullPath(m).TrimEnd(Path.DirectorySeparatorChar), nieuw, StringComparison.OrdinalIgnoreCase))
            .Where(m => File.Exists(Path.Combine(m, Databank.Bestandsnaam)) && File.Exists(Path.Combine(m, "organisatie.json")))
            .OrderByDescending(m => File.GetLastWriteTimeUtc(Path.Combine(m, Databank.Bestandsnaam)))
            .FirstOrDefault();
    }

    /// <summary>
    /// Kopieert de oude installatie naar de nieuwe gegevensmap, als die nog geen databank heeft. Geeft het aantal
    /// gekopieerde bestanden (0 als er niets te doen was).
    /// </summary>
    public static int Kopieer(string bron, string doel)
    {
        if (File.Exists(Path.Combine(doel, Databank.Bestandsnaam)))
        {
            return 0;
        }

        var aantal = 0;
        foreach (var bestand in Directory.EnumerateFiles(bron, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(bron, bestand);
            var naar = Path.Combine(doel, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(naar)!);
            using (var lees = new FileStream(bestand, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var schrijf = File.Create(naar))
            {
                lees.CopyTo(schrijf);
            }

            aantal++;
        }

        File.WriteAllText(Path.Combine(doel, Merkteken), $"{bron}{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}");
        return aantal;
    }

    /// <summary>Bij het opstarten: een oude installatie zoeken en overzetten als de nieuwe gegevensmap nog leeg is.</summary>
    public static string? ZetOverAlsNodig(string basis, string nieuweMap)
    {
        if (File.Exists(Path.Combine(nieuweMap, Databank.Bestandsnaam)) || Zoek(basis, nieuweMap) is not { } oud)
        {
            return null;
        }

        return Kopieer(oud, nieuweMap) > 0 ? oud : null;
    }
}
