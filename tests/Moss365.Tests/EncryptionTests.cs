using System.Security.Cryptography;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>R6: encrypted tenant databases (SQLCipher) and their keys.</summary>
public sealed class EncryptionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moss365-enc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void TheRecoveryKeyIsTheKeyWithACheck()
    {
        var key = TenantKeys.NewKey();
        var recovery = TenantKeys.RecoveryKey(key);
        Assert.Matches("^([A-Z2-9]{5}-){10}[A-Z2-9]{5}$", recovery);
        Assert.Equal(key, TenantKeys.FromRecoveryKey(recovery));
        Assert.Equal(key, TenantKeys.FromRecoveryKey(" " + recovery.ToLowerInvariant().Replace("-", " ") + " "));

        // A typo is caught by the check, not by a database that does not open
        var typo = (recovery[0] == 'A' ? "B" : "A") + recovery[1..];
        Assert.Null(TenantKeys.FromRecoveryKey(typo));
        Assert.Null(TenantKeys.FromRecoveryKey("geen sleutel"));
        Assert.Null(TenantKeys.FromRecoveryKey(null));
    }

    [Fact]
    public void APasswordWrapsTheKey()
    {
        var key = TenantKeys.NewKey();
        var info = TenantKeys.WithPassword(key, "een lang wachtwoord", iterations: 1000);
        Assert.Equal(EncryptionMethod.Password, info.Method);
        Assert.DoesNotContain(Convert.ToBase64String(key), info.Protected, StringComparison.Ordinal);
        Assert.Equal(key, TenantKeys.FromPassword(info, "een lang wachtwoord"));
        Assert.ThrowsAny<CryptographicException>(() => TenantKeys.FromPassword(info, "een ander wachtwoord"));
        Assert.NotEqual(info.Salt, TenantKeys.WithPassword(key, "een lang wachtwoord", 1000).Salt);

        Assert.NotNull(TenantKeys.CheckPassword("kort", "kort"));
        Assert.NotNull(TenantKeys.CheckPassword("een lang wachtwoord", "een lang wachtwoorD"));
        Assert.Null(TenantKeys.CheckPassword("een lang wachtwoord", "een lang wachtwoord"));
    }

    [Fact]
    public void AnEncryptedDatabaseHoldsNoReadableData()
    {
        var path = Path.Combine(_folder, "tenant.db");
        var key = TenantKeys.NewKey();
        var db = new Database(path, key);
        db.SaveDocument("geheim", "\"vertrouwelijke-inhoud\"");
        db.Compact();
        Assert.True(db.Encrypted);
        Assert.DoesNotContain("vertrouwelijke-inhoud", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.True(Database.NeedsKey(path));
        Assert.False(Database.Opens(path, TenantKeys.NewKey()));
        Assert.True(Database.Opens(path, key));
        Assert.Equal("\"vertrouwelijke-inhoud\"", new Database(path, key).ReadDocument("geheim"));
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => new Database(path));

        // A backup stays encrypted with the same key
        var backup = Path.Combine(_folder, "backup.db");
        db.BackupTo(backup);
        Assert.True(Database.NeedsKey(backup));
        Assert.True(Database.Opens(backup, key));
    }

    [Fact]
    public void ACopyWithAnotherKeyHoldsEverything()
    {
        var plain = new Database(Path.Combine(_folder, "plain.db"));
        plain.SaveDocument("a", "1");
        new DatabaseFileStore(plain).Write("inventory/x.csv", new byte[] { 1, 2, 3 });
        Assert.False(Database.NeedsKey(plain.FilePath));

        var key = TenantKeys.NewKey();
        var encrypted = Path.Combine(_folder, "enc.db");
        plain.CopyWithKey(encrypted, key);
        var e = new Database(encrypted, key);
        Assert.Equal(Database.Migrations.Count, e.Version);
        Assert.Equal("1", e.ReadDocument("a"));
        Assert.Equal(new byte[] { 1, 2, 3 }, new DatabaseFileStore(e).ReadBytes("inventory/x.csv"));

        var back = Path.Combine(_folder, "back.db");
        e.CopyWithKey(back, null);
        Assert.False(Database.NeedsKey(back));
        Assert.Equal("1", new Database(back).ReadDocument("a"));
    }
}
