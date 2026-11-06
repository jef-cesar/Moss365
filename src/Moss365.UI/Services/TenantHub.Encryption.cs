using System.Security.Cryptography;
using System.Text.Json;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// Encryption per tenant (R6, decision U7: off by default). The key of a tenant database is random; moss365.db only
/// keeps it protected by Windows (A: this Windows account, C: an AD group) or wrapped with a password (B). The recovery
/// key opens the database on its own. Keys stay in memory while the app is open and are never written to a log.
/// </summary>
public sealed partial class TenantHub
{
    private readonly IKeyProtector? _protector;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string EncryptionKey(string tenantId) => "encryption." + tenantId;

    /// <summary>How the database of a tenant is protected, or null when it is not encrypted.</summary>
    public EncryptionInfo? Encryption(string tenantId)
        => App.Setting(EncryptionKey(tenantId)) is { } json ? JsonSerializer.Deserialize<EncryptionInfo>(json) : null;

    private void SaveEncryption(string tenantId, EncryptionInfo? info)
        => App.SetSetting(EncryptionKey(tenantId), info is null ? null : JsonSerializer.Serialize(info));

    /// <summary>This method can be used on this PC (a password always; A and C need Windows).</summary>
    public bool CanProtect(EncryptionMethod method) => method == EncryptionMethod.Password || (_protector?.CanProtect(method) ?? false);

    /// <summary>The tenant cannot be opened without a password or the recovery key.</summary>
    public bool NeedsUnlock(TenantInfo tenant) => !TryKey(tenant, out _);

    /// <summary>The first tenant that opens without asking anything (to start with when the open one is locked).</summary>
    public TenantInfo? FirstOpenable() => App.Tenants().FirstOrDefault(t => !NeedsUnlock(t));

    private bool TryKey(TenantInfo tenant, out byte[]? key)
    {
        lock (_lock)
        {
            if (_keys.TryGetValue(tenant.Id, out var known))
            {
                key = known;
                return true;
            }
        }

        key = null;
        var path = TenantFolders.DatabasePath(_environment.Root, tenant.Id);
        var info = Encryption(tenant.Id);
        if (info is null)
        {
            // Not encrypted, unless the file says otherwise (for example after moss365.db was lost)
            return !Database.NeedsKey(path);
        }

        if (info.Method is EncryptionMethod.WindowsAccount or EncryptionMethod.AdGroup && _protector is not null)
        {
            try
            {
                var k = _protector.Unprotect(info.Method, Convert.FromBase64String(info.Protected));
                if (Database.Opens(path, k))
                {
                    lock (_lock)
                    {
                        _keys[tenant.Id] = k;
                    }

                    key = k;
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // Another Windows account, or no longer in the group: the recovery key still works
            }
        }

        return false;
    }

    /// <summary>Opens a locked tenant with its password or its recovery key. False when neither fits.</summary>
    public bool Unlock(string tenantId, string secret)
    {
        var info = Encryption(tenantId);
        var key = TenantKeys.FromRecoveryKey(secret);
        if (key is null && info?.Method == EncryptionMethod.Password)
        {
            try
            {
                key = TenantKeys.FromPassword(info, secret);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        if (key is null || !Database.Opens(TenantFolders.DatabasePath(_environment.Root, tenantId), key))
        {
            return false;
        }

        lock (_lock)
        {
            _keys[tenantId] = key;
        }

        return true;
    }

    /// <summary>A new key and the recovery key that goes with it (shown once, before encrypting).</summary>
    public static (byte[] Key, string RecoveryKey) NewKey()
    {
        var key = TenantKeys.NewKey();
        return (key, TenantKeys.RecoveryKey(key));
    }

    /// <summary>Protects a key with a method; checks right away that this user can unlock it again.</summary>
    private EncryptionInfo Protection(byte[] key, EncryptionMethod method, string? password, string? group)
    {
        switch (method)
        {
            case EncryptionMethod.Password:
                return TenantKeys.CheckPassword(password, password) is { } bad ? throw new ArgumentException(bad) : TenantKeys.WithPassword(key, password!);

            case EncryptionMethod.WindowsAccount or EncryptionMethod.AdGroup when _protector is not null && _protector.CanProtect(method):
            {
                string? sid = null;
                if (method == EncryptionMethod.AdGroup)
                {
                    sid = _protector.GroupSid((group ?? string.Empty).Trim()) ?? throw new ArgumentException($"De groep \"{group}\" is niet gevonden in AD.");
                }

                var blob = _protector.Protect(method, key, sid);
                byte[] back;
                try
                {
                    back = _protector.Unprotect(method, blob);
                }
                catch (CryptographicException ex)
                {
                    throw new ArgumentException(method == EncryptionMethod.AdGroup
                        ? "Je kan de sleutel zelf niet openen: zit je in de groep? Meld je na het toevoegen eerst af en weer aan."
                        : "Windows kon de sleutel niet beschermen: " + ex.Message);
                }

                if (!back.AsSpan().SequenceEqual(key))
                {
                    throw new ArgumentException("Windows gaf een andere sleutel terug.");
                }

                return new EncryptionInfo(method, Convert.ToBase64String(blob), Group: group?.Trim(), GroupSid: sid);
            }

            default:
                throw new ArgumentException("Deze manier van beveiligen kan niet op deze pc.");
        }
    }

    /// <summary>Encrypts a tenant database with the key (from <see cref="NewKey"/>), protected with the method.</summary>
    public void Encrypt(TenantInfo tenant, byte[] key, EncryptionMethod method, string? password = null, string? group = null)
    {
        if (Encryption(tenant.Id) is not null)
        {
            throw new InvalidOperationException("Deze tenant is al versleuteld.");
        }

        var protection = Protection(key, method, password, group);
        Reencrypt(tenant, key);
        SaveEncryption(tenant.Id, protection);
        Log(tenant, "Databank versleuteld ({Methode})", method);
    }

    /// <summary>Another way of protecting the same key (the database itself stays as it is).</summary>
    public void ChangeProtection(TenantInfo tenant, EncryptionMethod method, string? password = null, string? group = null)
    {
        if (!TryKey(tenant, out var key) || key is null)
        {
            throw new InvalidOperationException("Open de tenant eerst.");
        }

        SaveEncryption(tenant.Id, Protection(key, method, password, group));
        Log(tenant, "Beveiliging van de sleutel gewijzigd ({Methode})", method);
    }

    /// <summary>A new key (for example when the recovery key may be known to someone else); returns the new recovery key.</summary>
    public string RenewKey(TenantInfo tenant, string? password = null)
    {
        var info = Encryption(tenant.Id) ?? throw new InvalidOperationException("Deze tenant is niet versleuteld.");
        var (key, recovery) = NewKey();
        var protection = Protection(key, info.Method, password, info.Group);
        Reencrypt(tenant, key);
        SaveEncryption(tenant.Id, protection);
        Log(tenant, "Nieuwe sleutel voor de databank ({Methode})", info.Method);
        return recovery;
    }

    /// <summary>Turns encryption off again.</summary>
    public void Decrypt(TenantInfo tenant)
    {
        if (Encryption(tenant.Id) is null)
        {
            throw new InvalidOperationException("Deze tenant is niet versleuteld.");
        }

        Reencrypt(tenant, null);
        SaveEncryption(tenant.Id, null);
        Log(tenant, "Versleuteling van de databank uitgezet ({Methode})", EncryptionMethod.None);
    }

    /// <summary>
    /// Replaces the database by a checked copy with another key. The state of the tenant is closed (the screens reload);
    /// the copies made before migrations are removed, because they still have the old protection.
    /// </summary>
    private void Reencrypt(TenantInfo tenant, byte[]? newKey)
    {
        var state = Open(tenant);
        if (state.TaskBusy || state.ScriptBusy)
        {
            throw new InvalidOperationException("Wacht tot de taak of het script klaar is.");
        }

        var path = state.Database.FilePath;
        var copy = path + ".nieuw";
        state.Database.CopyWithKey(copy, newKey);
        var oldCopies = state.Database.MigrationCopies();

        lock (_lock)
        {
            _states.Remove(tenant.Id);
            state.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(copy, path, overwrite: true);
            foreach (var f in new[] { path + "-wal", path + "-shm" }.Where(File.Exists))
            {
                File.Delete(f);
            }

            foreach (var f in oldCopies)
            {
                f.Delete();
            }

            if (newKey is null)
            {
                _keys.Remove(tenant.Id);
            }
            else
            {
                _keys[tenant.Id] = newKey;
            }
        }
    }

    private void Log(TenantInfo tenant, string message, EncryptionMethod method)
    {
        var state = Open(tenant);
        state.Log.Information(message, method);
        state.Access(message.Replace(" ({Methode})", string.Empty, StringComparison.Ordinal), method.ToString());
    }
}
