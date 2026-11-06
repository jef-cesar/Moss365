using System.Security.Cryptography;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Storage;
using Moss365.UI.Components;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R6: an encrypted tenant only opens with the right key.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>Like DPAPI: only the same "Windows user" gets the key back; the group only for members.</summary>
    private sealed class FakeProtector : IKeyProtector
    {
        public byte User { get; set; } = 7;
        public bool InGroup { get; set; } = true;

        public bool CanProtect(EncryptionMethod method) => method is EncryptionMethod.WindowsAccount or EncryptionMethod.AdGroup;

        public byte[] Protect(EncryptionMethod method, byte[] key, string? groupSid)
            => new[] { (byte)method, User }.Concat(key.Select(b => (byte)(b ^ User))).ToArray();

        public byte[] Unprotect(EncryptionMethod method, byte[] blob)
        {
            if (blob[0] != (byte)method || (method == EncryptionMethod.WindowsAccount && blob[1] != User) || (method == EncryptionMethod.AdGroup && !InGroup))
            {
                throw new CryptographicException("niet voor deze gebruiker");
            }

            return blob.Skip(2).Select(b => (byte)(b ^ blob[1])).ToArray();
        }

        public string? GroupSid(string group) => group == @"TEST\GG-Moss365-Administrators" ? "S-1-5-21-1-2-3-1100" : null;
    }

    private (TenantHub Hub, FakeProtector Protector, HubEnvironment Environment) EncryptedHub(string name)
    {
        var environment = new HubEnvironment(Path.Combine(_base, name), Path.Combine(_base, "standaard.json"));
        var protector = new FakeProtector();
        var hub = new TenantHub(environment, (t, key) => new ManagementState(environment, null, null, t, key), protector);
        return (hub, protector, environment);
    }

    private static TenantHub Restart(HubEnvironment environment, IKeyProtector protector)
        => new(environment, (t, key) => new ManagementState(environment, null, null, t, key), protector);

    [Fact]
    public void WithTheWindowsAccountNothingIsAskedButAnotherUserIsLockedOut()
    {
        var (hub, protector, environment) = EncryptedHub("versleuteld-a");
        var tenant = hub.CurrentTenant;
        hub.Current.Database.SaveDocument("proef", "\"geheime-waarde\"");
        var path = hub.Current.Database.FilePath;
        File.WriteAllText(path + ".bak-20260101-000000", "oude kopie");

        var (key, recovery) = TenantHub.NewKey();
        hub.Encrypt(tenant, key, EncryptionMethod.WindowsAccount);
        Assert.True(Database.NeedsKey(path));
        Assert.DoesNotContain("geheime-waarde", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".bak-20260101-000000"));
        Assert.Equal("\"geheime-waarde\"", hub.Current.Database.ReadDocument("proef"));
        Assert.Contains(hub.Current.Database.ReadAccess(5), a => a.What == "Databank versleuteld");
        Assert.All(hub.Current.Database.ReadTechnicalLog(500), l => Assert.True(!l.Message.Contains(Convert.ToHexString(key), StringComparison.OrdinalIgnoreCase)
            && !l.Message.Contains(recovery[..11], StringComparison.Ordinal)));
        hub.Dispose();

        // Next start, same Windows user: opens by itself
        using (var again = Restart(environment, protector))
        {
            Assert.False(again.NeedsUnlock(tenant));
            Assert.Equal("\"geheime-waarde\"", again.Current.Database.ReadDocument("proef"));
        }

        // Another Windows user: locked; only the recovery key opens it
        protector.User = 9;
        using var other = Restart(environment, protector);
        Assert.True(other.NeedsUnlock(tenant));
        Assert.Throws<TenantLockedException>(() => other.Current);
        Assert.False(other.Unlock(tenant.Id, "fout"));
        Assert.True(other.Unlock(tenant.Id, recovery));
        Assert.Equal("\"geheime-waarde\"", other.Current.Database.ReadDocument("proef"));
    }

    [Fact]
    public void APasswordIsAskedAtEveryStart()
    {
        var (hub, protector, environment) = EncryptedHub("versleuteld-b");
        var tenant = hub.CurrentTenant;
        var (key, recovery) = TenantHub.NewKey();
        Assert.Throws<ArgumentException>(() => hub.Encrypt(tenant, key, EncryptionMethod.Password, "kort"));
        Assert.Null(hub.Encryption(tenant.Id));
        hub.Encrypt(tenant, key, EncryptionMethod.Password, "een lang wachtwoord");
        hub.Dispose();

        using var again = Restart(environment, protector);
        Assert.True(again.NeedsUnlock(tenant));
        Assert.Null(again.FirstOpenable());
        Assert.False(again.Unlock(tenant.Id, "een ander wachtwoord"));
        Assert.True(again.Unlock(tenant.Id, "een lang wachtwoord"));
        Assert.False(again.NeedsUnlock(tenant));

        // Change to the Windows account (same key, so the recovery key stays valid), then a new key, then off
        again.ChangeProtection(tenant, EncryptionMethod.WindowsAccount);
        Assert.Equal(EncryptionMethod.WindowsAccount, again.Encryption(tenant.Id)!.Method);
        Assert.True(Database.Opens(again.Current.Database.FilePath, TenantKeys.FromRecoveryKey(recovery)));
        var renewed = again.RenewKey(tenant);
        Assert.False(Database.Opens(again.Current.Database.FilePath, TenantKeys.FromRecoveryKey(recovery)));
        Assert.True(Database.Opens(again.Current.Database.FilePath, TenantKeys.FromRecoveryKey(renewed)));
        again.Decrypt(tenant);
        Assert.Null(again.Encryption(tenant.Id));
        Assert.False(Database.NeedsKey(again.Current.Database.FilePath));
    }

    [Fact]
    public void AnAdGroupOpensForItsMembers()
    {
        var (hub, protector, environment) = EncryptedHub("versleuteld-c");
        using var _ = hub;
        var tenant = hub.CurrentTenant;
        var (key, _) = TenantHub.NewKey();
        Assert.Throws<ArgumentException>(() => hub.Encrypt(tenant, key, EncryptionMethod.AdGroup, group: "BESTAAT-NIET"));
        protector.InGroup = false;
        Assert.Contains("zit je in de groep", Assert.Throws<ArgumentException>(() => hub.Encrypt(tenant, key, EncryptionMethod.AdGroup, group: @"TEST\GG-Moss365-Administrators")).Message, StringComparison.Ordinal);
        protector.InGroup = true;
        hub.Encrypt(tenant, key, EncryptionMethod.AdGroup, group: @"TEST\GG-Moss365-Administrators");
        Assert.Equal("S-1-5-21-1-2-3-1100", hub.Encryption(tenant.Id)!.GroupSid);

        protector.User = 9;
        using (var colleague = Restart(environment, protector))
        {
            Assert.False(colleague.NeedsUnlock(tenant));
        }

        protector.InGroup = false;
        using var gone = Restart(environment, protector);
        Assert.True(gone.NeedsUnlock(tenant));
    }

    [Fact]
    public void AnEncryptedBackupOnlyComesBackWithTheRecoveryKey()
    {
        var (hub, _, _) = EncryptedHub("versleuteld-backup");
        using var __ = hub;
        var tenant = hub.CurrentTenant;
        hub.Current.Database.SaveDocument("proef", "\"in-de-backup\"");
        var (key, recovery) = TenantHub.NewKey();
        hub.Encrypt(tenant, key, EncryptionMethod.Password, "een lang wachtwoord");
        var backup = hub.Backup(tenant, Path.Combine(_base, "backups"));
        Assert.True(Database.NeedsKey(backup));

        Assert.Contains("herstelsleutel", Assert.Throws<InvalidDataException>(() => hub.Restore(backup, "Terug")).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => hub.Restore(backup, "Terug", TenantKeys.RecoveryKey(TenantKeys.NewKey())));
        Assert.Single(hub.Tenants);

        // Restored with the recovery key: the new tenant is protected with this Windows account
        var restored = hub.Restore(backup, "Terug", recovery);
        Assert.Equal(EncryptionMethod.WindowsAccount, hub.Encryption(restored.Id)!.Method);
        Assert.Equal("\"in-de-backup\"", hub.Open(restored).Database.ReadDocument("proef"));
    }

    [Fact]
    public void ThePanelShowsTheRecoveryKeyBeforeEncrypting()
    {
        var (hub, _, _) = EncryptedHub("versleuteld-scherm");
        using var __ = hub;
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(hub.Current);
        var cut = ctx.RenderComponent<EncryptionPanel>();
        Assert.Contains("Uit.", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").First(b => b.TextContent == "Versleutelen").Click();
        cut.FindAll("input[type=radio]")[1].Change(true); // password
        cut.Find("input[aria-label='Wachtwoord']").Change("een lang wachtwoord");
        cut.Find("input[aria-label='Wachtwoord nog eens']").Change("een lang wachtwoord");
        cut.FindAll("button").First(b => b.TextContent == "Volgende").Click();

        // The recovery key; encrypting only after typing its last group
        var recovery = cut.Find("code.kb-herstelsleutel").TextContent;
        Assert.NotNull(TenantKeys.FromRecoveryKey(recovery));
        var encrypt = cut.FindAll("button").First(b => b.TextContent.StartsWith("Versleutelen in", StringComparison.Ordinal));
        Assert.True(encrypt.HasAttribute("disabled"));
        Assert.Null(hub.Encryption(hub.CurrentTenant.Id));
        cut.Find("input[aria-label='Laatste groep van de herstelsleutel']").Input(recovery[(recovery.LastIndexOf('-') + 1)..]);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Versleutelen in", StringComparison.Ordinal)).Click();

        Assert.Equal(EncryptionMethod.Password, hub.Encryption(hub.CurrentTenant.Id)!.Method);
        Assert.True(Database.Opens(hub.Current.Database.FilePath, TenantKeys.FromRecoveryKey(recovery)));
    }
}
