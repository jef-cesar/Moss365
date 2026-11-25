using System.Runtime.Versioning;
using System.Security.Cryptography;
using Moss365.Core.Changes;

namespace Moss365.Connections.Check;

/// <summary>
/// The kept sign-in (0.33), encrypted with the Windows account of this PC (DPAPI) before it goes into the tenant
/// database; another Windows account or another PC cannot read it and simply signs in again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedTokenStore : ITokenCacheStore
{
    private static readonly byte[] Entropy = "Moss365.SignIn.v1"u8.ToArray();
    private readonly ITokenCacheStore _inner;

    public ProtectedTokenStore(ITokenCacheStore inner) => _inner = inner;

    public byte[]? Load()
    {
        if (_inner.Load() is not { } blob)
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Another Windows account or PC: forget it, the next sign-in keeps a new one
            _inner.Clear();
            return null;
        }
    }

    public void Save(byte[] data) => _inner.Save(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));

    public void Clear() => _inner.Clear();
}
