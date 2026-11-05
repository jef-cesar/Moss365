using Microsoft.AspNetCore.Components;

namespace Moss365.UI.Services;

/// <summary>
/// Basis voor schermen die gegevens tonen: als de gegevens op de achtergrond veranderen (live laden, vernieuwen na een
/// wijziging), rekent het scherm opnieuw (<see cref="ComponentBase.OnParametersSet"/>) en tekent het zich opnieuw.
/// </summary>
public abstract class VerversendePagina : ComponentBase, IDisposable
{
    private bool _ingeschreven;
    private object? _laatsteData;

    [Inject] protected BeheerState State { get; set; } = default!;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_ingeschreven)
        {
            State.Gewijzigd += Ververs;
            _ingeschreven = true;
        }

        return base.SetParametersAsync(parameters);
    }

    private void Ververs() => _ = InvokeAsync(() =>
    {
        // Enkel opnieuw rekenen als de gegevens zelf veranderd zijn, niet bij elke voortgangsmelding
        if (!ReferenceEquals(_laatsteData, State.Data))
        {
            _laatsteData = State.Data;
            OnParametersSet();
        }

        StateHasChanged();
    });

    public virtual void Dispose()
    {
        if (_ingeschreven)
        {
            State.Gewijzigd -= Ververs;
        }
    }
}
