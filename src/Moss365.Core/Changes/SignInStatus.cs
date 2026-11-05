namespace Moss365.Core.Changes;

/// <summary>
/// A sign-in at Microsoft in progress: the app opens no browser itself, but shows the link so that the administrator
/// can paste it into the browser of choice. After signing in the link points back to http://localhost on this PC.
/// </summary>
public sealed class SignInStatus
{
    private readonly object _lock = new();
    private CancellationTokenSource? Cancel_;

    public event Action? Changed;

    /// <summary>The sign-in link, or null when no sign-in is in progress.</summary>
    public Uri? Link { get; private set; }

    public DateTimeOffset? Since { get; private set; }

    /// <summary>Called by the connection with the link and the cancellation of the sign-in in progress.</summary>
    public void Show(Uri link, CancellationTokenSource cancel)
    {
        lock (_lock)
        {
            Link = link;
            Since = DateTimeOffset.Now;
            Cancel_ = cancel;
        }

        Changed?.Invoke();
    }

    public void Done()
    {
        lock (_lock)
        {
            Link = null;
            Since = null;
            Cancel_ = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Aborts the sign-in in progress (the action that waited for it then fails with "cancelled").</summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = Cancel_;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sign-in was just done
        }
    }
}
