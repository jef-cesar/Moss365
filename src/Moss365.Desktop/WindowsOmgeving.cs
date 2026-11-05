using System.Diagnostics;
using System.IO;
using Moss365.UI.Services;

namespace Moss365.Desktop;

/// <summary>De mappen van de app op Windows; exports komen in Documenten\Moss365.</summary>
public sealed class WindowsOmgeving : IOmgeving
{
    private static readonly string Lokaal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moss365");

    /// <summary>De map van een vorige installatie die bij deze start overgezet werd, of null.</summary>
    public static string? OvergezetVan { get; } = ZetOudeInstallatieOver();

    private static string? ZetOudeInstallatieOver()
    {
        try
        {
            return Moss365.Core.Opslag.OudeInstallatie.ZetOverAlsNodig(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Lokaal);
        }
        catch (Exception)
        {
            return null; // Overzetten is een gemak; mislukt het, dan start de app gewoon leeg
        }
    }

    public WindowsOmgeving()
    {
        _ = OvergezetVan;
    }

    public string InventarisBasis { get; } = Path.Combine(Lokaal, "Inventaris");

    public string ConfigStandaardPad { get; } = StandaardConfig();

    public string ConfigGebruikersPad { get; } = Path.Combine(Lokaal, "organisatie.json");

    public string ExportMap { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Moss365");

    /// <summary>
    /// De meegeleverde organisatie.json naast de exe; ontbreekt die (bv. Moss365.exe rechtstreeks uit de zip gestart),
    /// dan de kopie die in de exe zit, uitgepakt in de gegevensmap.
    /// </summary>
    private static string StandaardConfig()
    {
        var naastExe = Path.Combine(AppContext.BaseDirectory, "config", "organisatie.json");
        if (File.Exists(naastExe))
        {
            return naastExe;
        }

        try
        {
            using var bron = typeof(WindowsOmgeving).Assembly.GetManifestResourceStream("organisatie.json");
            if (bron is null)
            {
                return naastExe;
            }

            Directory.CreateDirectory(Lokaal);
            var kopie = Path.Combine(Lokaal, "organisatie-meegeleverd.json");
            using (var doel = File.Create(kopie))
            {
                bron.CopyTo(doel);
            }

            return kopie;
        }
        catch (Exception)
        {
            return naastExe;
        }
    }

    public void ToonBestand(string pad)
    {
        if (File.Exists(pad))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pad}\"") { UseShellExecute = false });
        }
        else if (Directory.Exists(pad))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pad}\"") { UseShellExecute = false });
        }
    }
}
