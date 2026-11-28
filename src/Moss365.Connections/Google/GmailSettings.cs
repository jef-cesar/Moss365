using Moss365.Core.Changes;

namespace Moss365.Connections.Google;

/// <summary>The Gmail settings of someone else (G2, part Gmail settings, U34): filled in with the service account.</summary>
public sealed partial class GoogleConnection
{
    private partial Task<ChangeResult> GmailWriteAsync(Change w, CancellationToken cancel)
        => Task.FromResult(ChangeResult.Error(w, Core.Localization.Texts.T["gmail.no-service-account"]));
}
