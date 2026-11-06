using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Moss365.Core.Storage;

namespace Moss365.Connections.Check;

/// <summary>
/// Protects the key of a tenant database with Windows (R6): A with DPAPI for the signed-in user, C with DPAPI-NG for
/// an AD group (every member can unlock it on a domain PC). Nothing is written anywhere by this class.
/// </summary>
public sealed class WindowsKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = "Moss365 tenant key v1"u8.ToArray();

    public bool CanProtect(EncryptionMethod method) => OperatingSystem.IsWindows() && method switch
    {
        EncryptionMethod.WindowsAccount => true,
        EncryptionMethod.AdGroup => OperatingSystem.IsWindowsVersionAtLeast(6, 2) && LocalDomainFinder.Find() is not null,
        _ => false,
    };

    public byte[] Protect(EncryptionMethod method, byte[] key, string? groupSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return method switch
        {
            EncryptionMethod.WindowsAccount => ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser),
            EncryptionMethod.AdGroup => DpapiNg.Protect(key, groupSid ?? throw new ArgumentException("Geen groep.")),
            _ => throw new ArgumentException("Deze manier gebruikt Windows niet.", nameof(method)),
        };
    }

    public byte[] Unprotect(EncryptionMethod method, byte[] blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CryptographicException("Enkel op Windows.");
        }

        return method switch
        {
            EncryptionMethod.WindowsAccount => ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser),
            EncryptionMethod.AdGroup => DpapiNg.Unprotect(blob),
            _ => throw new CryptographicException("Deze manier gebruikt Windows niet."),
        };
    }

    public string? GroupSid(string group)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        try
        {
            var sid = (SecurityIdentifier)new NTAccount(group.Trim()).Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>DPAPI-NG (ncrypt.dll): a secret protected for "SID=..." can be opened by every member of that group.</summary>
    [SupportedOSPlatform("windows")]
    private static class DpapiNg
    {
        private const uint SilentFlag = 0x00000040;

        public static byte[] Protect(byte[] data, string sid)
        {
            Check(NCryptCreateProtectionDescriptor("SID=" + sid, 0, out var descriptor), "beschermen voor de groep");
            try
            {
                Check(NCryptProtectSecret(descriptor, SilentFlag, data, (uint)data.Length, IntPtr.Zero, IntPtr.Zero, out var blob, out var length), "beschermen voor de groep");
                return Take(blob, length);
            }
            finally
            {
                NCryptCloseProtectionDescriptor(descriptor);
            }
        }

        public static byte[] Unprotect(byte[] blob)
        {
            var status = NCryptUnprotectSecret(IntPtr.Zero, SilentFlag, blob, (uint)blob.Length, IntPtr.Zero, IntPtr.Zero, out var data, out var length);
            if (status != 0)
            {
                throw new CryptographicException($"De sleutel kon niet geopend worden voor deze gebruiker (0x{status:X8}).");
            }

            return Take(data, length);
        }

        private static byte[] Take(IntPtr memory, uint length)
        {
            try
            {
                var output = new byte[length];
                Marshal.Copy(memory, output, 0, (int)length);
                return output;
            }
            finally
            {
                LocalFree(memory);
            }
        }

        private static void Check(int status, string what)
        {
            if (status != 0)
            {
                throw new CryptographicException($"DPAPI-NG: {what} mislukt (0x{status:X8}).");
            }
        }

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptCreateProtectionDescriptor(string descriptorString, uint flags, out IntPtr descriptor);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptCloseProtectionDescriptor(IntPtr descriptor);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptProtectSecret(IntPtr descriptor, uint flags, byte[] data, uint dataLength, IntPtr memPara, IntPtr window, out IntPtr blob, out uint blobLength);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptUnprotectSecret(IntPtr descriptor, uint flags, byte[] blob, uint blobLength, IntPtr memPara, IntPtr window, out IntPtr data, out uint dataLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
