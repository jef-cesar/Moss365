using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>How the key of an encrypted tenant database is protected (R6, decision U7: off by default).</summary>
public enum EncryptionMethod
{
    None,

    /// <summary>A: the key is encrypted with the Windows sign-in of this user (DPAPI).</summary>
    WindowsAccount,

    /// <summary>B: a password when opening; the key is wrapped with a key derived from it.</summary>
    Password,

    /// <summary>C: the key is encrypted for an AD group (DPAPI-NG): every member can open it on a domain PC.</summary>
    AdGroup,
}

/// <summary>Protects a key with Windows (DPAPI for the user, DPAPI-NG for an AD group).</summary>
public interface IKeyProtector
{
    bool CanProtect(EncryptionMethod method);

    byte[] Protect(EncryptionMethod method, byte[] key, string? groupSid);

    /// <summary>The key again; throws <see cref="CryptographicException"/> when this user may not.</summary>
    byte[] Unprotect(EncryptionMethod method, byte[] blob);

    /// <summary>The SID of an AD group (DOMAIN\name or name), or null when it is not found.</summary>
    string? GroupSid(string group);
}

/// <summary>
/// How the key of one tenant is kept, outside its database (in moss365.db). Never the key itself: only the key
/// protected by Windows, or wrapped with a password.
/// </summary>
public sealed record EncryptionInfo(EncryptionMethod Method, string Protected, string? Salt = null, int Iterations = 0, string? Group = null, string? GroupSid = null)
{
    public DateTimeOffset Since { get; init; } = DateTimeOffset.Now;
}

/// <summary>A tenant whose database cannot be opened without a password or the recovery key.</summary>
public sealed class TenantLockedException(string tenantId, EncryptionMethod method)
    : InvalidOperationException("Deze tenant is versleuteld: geef het wachtwoord of de herstelsleutel.")
{
    public string TenantId { get; } = tenantId;
    public EncryptionMethod Method { get; } = method;
}

/// <summary>
/// The keys of encrypted tenant databases. Every tenant has its own random 256-bit key. The recovery key is that key
/// itself (with a check), so that it opens the database without anything else, also on another PC or after
/// moss365.db is lost.
/// </summary>
public static class TenantKeys
{
    public const int KeySize = 32;

    /// <summary>PBKDF2-SHA256 iterations for a password (OWASP 2023).</summary>
    public const int Iterations = 600_000;

    public const int MinimumPasswordLength = 12;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly byte[] AssociatedData = Encoding.ASCII.GetBytes("Moss365 tenant key v1");

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>The PRAGMA that opens a database with this key (a raw key: no key derivation at every open).</summary>
    public static string Pragma(byte[] key) => $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";

    internal static string AttachKey(byte[]? key) => key is null ? "''" : $"\"x'{Convert.ToHexString(key)}'\"";

    /// <summary>The recovery key to write down: the key and two check bytes, in groups of five characters.</summary>
    public static string RecoveryKey(byte[] key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("Verkeerde sleutel.", nameof(key));
        }

        var text = Base32(key.Concat(SHA256.HashData(key).Take(2)).ToArray());
        return string.Join('-', Enumerable.Range(0, (text.Length + 4) / 5).Select(i => text.Substring(i * 5, Math.Min(5, text.Length - i * 5))));
    }

    /// <summary>The key in a recovery key, or null when it is not one (or has a typo).</summary>
    public static byte[]? FromRecoveryKey(string? text)
    {
        var clean = new string((text ?? string.Empty).ToUpperInvariant().Where(c => c is not ('-' or ' ')).ToArray());
        var bytes = FromBase32(clean);
        if (bytes is not { Length: KeySize + 2 })
        {
            return null;
        }

        var key = bytes[..KeySize];
        return SHA256.HashData(key).AsSpan(0, 2).SequenceEqual(bytes.AsSpan(KeySize)) ? key : null;
    }

    /// <summary>Why a new password is not good enough, or null.</summary>
    public static string? CheckPassword(string? password, string? again)
    {
        if ((password ?? string.Empty).Length < MinimumPasswordLength)
        {
            return $"Kies een wachtwoord van minstens {MinimumPasswordLength} tekens.";
        }

        return password == again ? null : "De twee wachtwoorden verschillen.";
    }

    /// <summary>Wraps the key with a password (PBKDF2-SHA256, AES-256-GCM).</summary>
    public static EncryptionInfo WithPassword(byte[] key, string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrapping = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(wrapping, tag.Length))
        {
            aes.Encrypt(nonce, key, cipher, tag, AssociatedData);
        }

        CryptographicOperations.ZeroMemory(wrapping);
        return new EncryptionInfo(EncryptionMethod.Password, Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray()), Convert.ToBase64String(salt), iterations);
    }

    /// <summary>The key from a password; throws <see cref="CryptographicException"/> when the password is wrong.</summary>
    public static byte[] FromPassword(EncryptionInfo info, string password)
    {
        if (info.Method != EncryptionMethod.Password || info.Salt is null)
        {
            throw new CryptographicException("Deze tenant gebruikt geen wachtwoord.");
        }

        var blob = Convert.FromBase64String(info.Protected);
        var wrapping = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(info.Salt), info.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            var key = new byte[blob.Length - 28];
            using var aes = new AesGcm(wrapping, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, key.Length), blob.AsSpan(12 + key.Length), key, AssociatedData);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
        }
    }

    private static string Base32(byte[] data)
    {
        var output = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    private static byte[]? FromBase32(string text)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 255));
                bits -= 8;
            }
        }

        return output.ToArray();
    }
}

/// <summary>Opening, checking and converting encrypted databases (SQLCipher).</summary>
public sealed partial class Database
{
    /// <summary>
    /// The file can only be opened with a key (an encrypted database, or no SQLite database at all). A missing or empty
    /// file needs none.
    /// </summary>
    public static bool NeedsKey(string path) => File.Exists(path) && new FileInfo(path).Length > 0 && !Opens(path, null);

    /// <summary>The file opens with this key (null: without a key).</summary>
    public static bool Opens(string path, byte[]? key)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            if (key is not null)
            {
                Run(c, TenantKeys.Pragma(key));
            }

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            return false;
        }
    }

    /// <summary>
    /// A copy of this database with another key (null: not encrypted), for turning encryption on or off or renewing the
    /// key. The copy is checked before it is returned; the database itself is not changed.
    /// </summary>
    public void CopyWithKey(string target, byte[]? key)
    {
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        lock (_lock)
        {
            using var c = Open();
            Run(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            var version = ReadVersion(c);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"ATTACH DATABASE $p AS target KEY {TenantKeys.AttachKey(key)};";
                cmd.Parameters.AddWithValue("$p", target);
                cmd.ExecuteNonQuery();
            }

            Run(c, "SELECT sqlcipher_export('target');");
            Run(c, $"PRAGMA target.user_version = {version};");
            Run(c, "DETACH DATABASE target;");
        }

        // The copy must open with the new key, be whole and hold the same rows
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        check.Open();
        if (key is not null)
        {
            Run(check, TenantKeys.Pragma(key));
        }

        using (var cmd = check.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            if (cmd.ExecuteScalar() as string != "ok")
            {
                throw new InvalidDataException("De versleutelde kopie is niet in orde; er werd niets gewijzigd.");
            }
        }

        foreach (var table in new[] { "Logboek", "Document", "File", "Version", "Snapshot" })
        {
            if (RowCount(check, table) != Count(table))
            {
                throw new InvalidDataException($"De kopie mist rijen ({table}); er werd niets gewijzigd.");
            }
        }
    }

    private long Count(string table)
    {
        lock (_lock)
        {
            using var c = Open();
            return RowCount(c, table);
        }
    }

    private static long RowCount(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
