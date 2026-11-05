using Moss365.Core.Scripts;
using Moss365.Core.Wijzigingen;

namespace Moss365.UI.Services;

/// <summary>Een script dat draait (of net klaar is): de gebeurtenissen, de voortgang en het resultaat.</summary>
public sealed class ScriptRunStatus
{
    private readonly object _slot = new();
    private readonly List<ScriptGebeurtenis> _gebeurtenissen = new();

    public required ScriptBestand Script { get; init; }
    public required ScriptRun Run { get; set; }
    public CancellationTokenSource Annuleren { get; } = new();
    public bool Klaar { get; set; }
    public string? Voortgang { get; set; }
    public int? Procent { get; set; }

    public IReadOnlyList<ScriptGebeurtenis> Gebeurtenissen
    {
        get
        {
            lock (_slot)
            {
                return _gebeurtenissen.ToList();
            }
        }
    }

    public void Voegtoe(ScriptGebeurtenis g)
    {
        lock (_slot)
        {
            _gebeurtenissen.Add(g);
        }
    }
}

public sealed partial class BeheerState
{
    private DateTime _laatsteMelding = DateTime.MinValue;

    /// <summary>Waarden om het volgende startformulier mee in te vullen (Opnieuw starten met dezelfde waarden).</summary>
    public IReadOnlyDictionary<string, string>? ScriptVoorinvulling { get; set; }

    public ScriptMappen ScriptMappen => new(_omgeving.IngebouwdeScripts, _omgeving.ScriptsGegevens);

    /// <summary>Het script dat nu draait of als laatste draaide (één tegelijk).</summary>
    public ScriptRunStatus? ScriptRun { get; private set; }

    public bool ScriptBezig => ScriptRun is { Klaar: false };

    /// <summary>Scripts kunnen draaien (Windows-app met PowerShell); in de testomgeving een nepuitvoerder.</summary>
    public bool ScriptsBeschikbaar => _fabriek?.Scripts(Instellingen, AdWachtwoord) is not null;

    public IReadOnlyList<ScriptBestand> LeesScripts() => ScriptBibliotheek.Laad(ScriptMappen);

    public ScriptBestand? ZoekScript(string sleutel)
        => LeesScripts().FirstOrDefault(s => string.Equals(s.Sleutel, sleutel, StringComparison.OrdinalIgnoreCase));

    private void RuimScriptRunsOp()
    {
        try
        {
            ScriptRuns.Opruimen(ScriptMappen, DateTimeOffset.Now);
        }
        catch (IOException)
        {
            // Volgende keer opnieuw
        }
    }

    public async Task<IReadOnlyList<ScriptGebeurtenis>> ControleerScriptAsync(string tekst)
        => _fabriek?.Scripts(Instellingen, AdWachtwoord) is { } u ? await u.ControleerAsync(tekst, CancellationToken.None) : Array.Empty<ScriptGebeurtenis>();

    /// <summary>
    /// Start een script op de achtergrond. Bij een eenmalig script (niet opgeslagen) is de tekst gegeven: die komt in
    /// de map van de run en volledig in het logboek. Geeft de status terug, of null met een fout.
    /// </summary>
    public (ScriptRunStatus? Status, string? Fout) StartScript(ScriptBestand script, IReadOnlyDictionary<string, string> waarden, bool voorbeeld, string? eenmaligeTekst = null, Guid? gepland = null)
    {
        if (ScriptBezig)
        {
            return (null, "Er draait al een script. Wacht tot het klaar is, of annuleer het.");
        }

        if (_fabriek?.Scripts(Instellingen, AdWachtwoord) is not { } uitvoerder)
        {
            return (null, "Scripts kunnen hier niet draaien (enkel in de Windows-app).");
        }

        if (eenmaligeTekst is null && ScriptBibliotheek.IsGewijzigdBuitenApp(ScriptMappen, script))
        {
            return (null, "Dit script werd buiten de app gewijzigd. Bekijk het en keur het goed (Bewerken > Opslaan) voor het mag draaien.");
        }

        var fouten = ScriptWaarden.Controleer(script.Kop, waarden);
        if (fouten.Count > 0)
        {
            return (null, string.Join(" ", fouten));
        }

        var schoon = script.Kop.Variabelen
            .Where(v => waarden.ContainsKey(v.Naam))
            .ToDictionary(v => v.Naam, v => waarden[v.Naam], StringComparer.OrdinalIgnoreCase);
        var sleutel = eenmaligeTekst is null ? script.Sleutel : "eenmalig";
        var run = new ScriptRun
        {
            Sleutel = sleutel,
            Naam = script.Kop.Naam,
            Vingerafdruk = eenmaligeTekst is null ? script.Vingerafdruk : ScriptBibliotheek.Vingerafdruk(eenmaligeTekst),
            Wie = Gebruiker,
            Voorbeeld = voorbeeld,
            Eenmalig = eenmaligeTekst is not null,
            Waarden = new Dictionary<string, string>(schoon),
        };
        run = run with { Map = ScriptRuns.NieuweMap(ScriptMappen, sleutel, run.Id) };

        // Een eenmalig script draait vanuit de map van de run (zo blijft de tekst bij de uitvoer)
        if (eenmaligeTekst is not null)
        {
            var pad = Path.Combine(run.Map, "script.ps1");
            File.WriteAllText(pad, eenmaligeTekst, new System.Text.UTF8Encoding(true));
            script = ScriptBibliotheek.LeesBestand(pad, false);
        }

        ScriptRuns.Bewaar(run);
        if (eenmaligeTekst is null)
        {
            ScriptWaarden.Bewaar(ScriptMappen, script.Sleutel, script.Kop, schoon);
        }

        var status = new ScriptRunStatus { Script = script, Run = run };
        ScriptRun = status;
        Gewijzigd?.Invoke();
        _ = DraaiAsync(uitvoerder, status, schoon, voorbeeld, eenmaligeTekst, gepland);
        return (status, null);
    }

    public void AnnuleerScript()
    {
        if (ScriptRun is { Klaar: false } s)
        {
            s.Annuleren.Cancel();
        }
    }

    private async Task DraaiAsync(IScriptUitvoerder uitvoerder, ScriptRunStatus status, Dictionary<string, string> waarden, bool voorbeeld, string? eenmaligeTekst, Guid? gepland)
    {
        // Synchroon en in volgorde (Progress<T> zou via de threadpool melden, ook nog na het einde van het script)
        var melder = new ScriptMelder(g =>
        {
            status.Voegtoe(g);
            if (g.Soort == GebeurtenisSoort.Voortgang)
            {
                status.Voortgang = g.Klaar ? null : g.Tekst;
                status.Procent = g.Klaar ? null : g.Procent;
            }

            // Niet bij elke regel hertekenen
            if ((DateTime.Now - _laatsteMelding).TotalMilliseconds > 150)
            {
                _laatsteMelding = DateTime.Now;
                Gewijzigd?.Invoke();
            }
        });

        var code = -1;
        string? fout = null;
        var geannuleerd = false;
        try
        {
            var opdracht = new ScriptOpdracht(status.Script, waarden, voorbeeld, status.Run.Map);
            code = await Task.Run(() => uitvoerder.VoerUitAsync(opdracht, melder, status.Annuleren.Token));
        }
        catch (OperationCanceledException)
        {
            geannuleerd = true;
        }
        catch (Exception ex)
        {
            fout = ex.Message;
        }

        var lijst = status.Gebeurtenissen;
        var aantalFouten = lijst.Count(g => g.Soort == GebeurtenisSoort.Fout);
        var (resultaat, melding) = fout is not null ? (RunResultaat.Fout, fout) : ScriptUitvoer.Resultaat(code, aantalFouten, lijst.Count(g => g.Soort == GebeurtenisSoort.Waarschuwing), geannuleerd);
        status.Run = status.Run with
        {
            Einde = DateTimeOffset.Now,
            Resultaat = resultaat,
            Melding = melding,
            Fouten = aantalFouten,
            Waarschuwingen = lijst.Count(g => g.Soort == GebeurtenisSoort.Waarschuwing),
        };
        ScriptRuns.Bewaar(status.Run);
        status.Voortgang = null;

        var parameters = new Dictionary<string, string>(waarden, StringComparer.OrdinalIgnoreCase)
        {
            ["Vingerafdruk"] = status.Run.Vingerafdruk,
            ["Run"] = status.Run.Map,
        };
        if (eenmaligeTekst is not null)
        {
            parameters["Script"] = eenmaligeTekst;
        }

        Logboek.Schrijf(new LogRegel(DateTimeOffset.Now, Gebruiker, eenmaligeTekst is null ? "Script" : "Eenmalig script", "Script",
            voorbeeld ? "ScriptVoorbeeld" : "ScriptUitvoeren", status.Run.Sleutel, status.Run.Naam,
            (voorbeeld ? "Voorbeeld (-WhatIf) van " : "Script ") + status.Run.Naam + " (kan niet automatisch ongedaan gemaakt worden)",
            parameters, resultaat is RunResultaat.Gelukt or RunResultaat.Waarschuwingen ? "Gelukt" : resultaat == RunResultaat.Geannuleerd ? "Geannuleerd" : "Mislukt", melding));

        // Een ingepland script: na een echte run de volgende keer plannen (of weghalen)
        if (gepland is { } id && !voorbeeld && Gepland.Lees().FirstOrDefault(a => a.Id == id) is { } actie)
        {
            if (actie.VolgendeKeer(DateTimeOffset.Now) is { } volgende)
            {
                actie.Wanneer = volgende;
                Gepland.Bewaar(Gepland.Lees().Where(a => a.Id != id).Append(actie));
            }
            else
            {
                Gepland.Verwijder(id);
            }
        }

        // Pas klaar als alles bewaard en gelogd is
        status.Klaar = true;
        Gewijzigd?.Invoke();
    }

    private sealed class ScriptMelder(Action<ScriptGebeurtenis> doe) : IProgress<ScriptGebeurtenis>
    {
        public void Report(ScriptGebeurtenis value) => doe(value);
    }
}
