using Microsoft.AspNetCore.Components;

namespace Moss365.UI.Services;

/// <summary>
/// Base for screens that show data: when the data changes in the background (live loading, refreshing after a change),
/// the screen recalculates (<see cref="ComponentBase.OnParametersSet"/>) and redraws itself.
/// </summary>
public abstract class RefreshingPage : ComponentBase, IDisposable
{
    private bool _subscribed;
    private object? _lastData;

    [Inject] protected ManagementState State { get; set; } = default!;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_subscribed)
        {
            State.Changed += Reload;
            _subscribed = true;
        }

        return base.SetParametersAsync(parameters);
    }

    private void Reload() => _ = InvokeAsync(() =>
    {
        // Only recalculate when the data itself changed, not on every progress message
        if (!ReferenceEquals(_lastData, State.Data))
        {
            _lastData = State.Data;
            OnParametersSet();
        }

        StateHasChanged();
    });

    public virtual void Dispose()
    {
        if (_subscribed)
        {
            State.Changed -= Reload;
        }
    }
}
