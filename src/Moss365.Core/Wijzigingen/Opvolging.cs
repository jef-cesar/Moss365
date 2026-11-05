using System.Text.Encodings.Web;
using System.Text.Json;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Stappen die moeten wachten tot een nieuwe gebruiker naar Entra gesynchroniseerd is (licentie, cloudgroepen).
/// De app bewaart ze en voert ze uit zodra de gebruiker in Entra bestaat.
/// </summary>
public sealed class OpvolgTaak
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Aangemaakt { get; init; } = DateTimeOffset.Now;
    public required string Upn { get; init; }
    public required string Omschrijving { get; init; }
    public List<Wijziging> Wijzigingen { get; init; } = new();
    public DateTimeOffset? LaatstGecontroleerd { get; set; }
    public string? LaatsteMelding { get; set; }
}

public sealed class OpvolgOpslag
{
    private static readonly JsonSerializerOptions Opties = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Opslag.ITekstOpslag _opslag;

    public OpvolgOpslag(string pad) : this(new Opslag.BestandTekst(pad))
    {
    }

    /// <summary>Sinds 0.8 in de databank (document "opvolging").</summary>
    public OpvolgOpslag(Opslag.ITekstOpslag opslag) => _opslag = opslag;

    public List<OpvolgTaak> Lees()
        => _opslag.Lees() is { } json ? JsonSerializer.Deserialize<List<OpvolgTaak>>(json, Opties) ?? new List<OpvolgTaak>() : new List<OpvolgTaak>();

    public void Bewaar(IEnumerable<OpvolgTaak> taken) => _opslag.Bewaar(JsonSerializer.Serialize(taken.ToList(), Opties));

    public void Voegtoe(OpvolgTaak taak)
    {
        var taken = Lees();
        taken.Add(taak);
        Bewaar(taken);
    }
}
