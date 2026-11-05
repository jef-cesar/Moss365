using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Connections.Ad;

/// <summary>
/// Reads and writes on-premises AD via LDAP (System.DirectoryServices). Also works on a PC outside the domain with an
/// explicit domain controller and an AD account. Traffic is signed and encrypted (Kerberos/NTLM sealing).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdLdap : IAdConnection, IExecutor
{
    private const AuthenticationTypes Secured = AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

    // HRESULT of "the attribute or value already exists" (member of a group it already is a member of)
    private const int AlreadyPresent = unchecked((int)0x80071392);

    private readonly string _server;
    private readonly string? _user;
    private readonly string? _password;

    public AdLdap(string server, string? user, string? password)
    {
        _server = server;
        _user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        _password = _user is null ? null : password;
    }

    public TargetSystem TargetSystem => TargetSystem.ActiveDirectory;

    private DirectoryEntry Item(string dn) => new(AdLdapMapper.LdapPath(_server, dn), _user, _password, Secured);

    public string ReadDomainDn()
    {
        using var root = Item("RootDSE");
        return root.Properties["defaultNamingContext"].Value?.ToString()
               ?? throw new InvalidOperationException("De domeincontroller gaf geen domein terug.");
    }

    public Task<AdData> ReadAsync(IProgress<string>? progress, CancellationToken cancel) => ReadAsync(null, progress, cancel);

    private const string UserFilter = "(&(objectCategory=person)(objectClass=user))";
    private const string GroupFilter = "(objectClass=group)";

    public Task<AdData> ReadAsync(AdBase? previous, IProgress<string>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        progress?.Report("AD: verbinden met " + _server + "...");
        var domainDn = ReadDomainDn();

        // The state before reading: what changes during the read comes along again the next time
        var snapshot = ReadSnapshot();
        using var baseline = Item(domainDn);

        if (previous is not null && snapshot is not null && AdLdapMapper.CanContinueFrom(previous.Snapshot, snapshot))
        {
            var since = $"(uSNChanged>={previous.Snapshot.HighestUsn + 1})";
            progress?.Report("AD: enkel de wijzigingen sinds de vorige lezing...");
            var changedUsers = Find(baseline, $"(&{UserFilter}{since})", AdLdapMapper.UserAttributes, cancel)
                .Select(AdLdapMapper.ToUser).ToList();
            var changedGroups = Find(baseline, $"(&{GroupFilter}{since})", AdLdapMapper.GroupAttributes, cancel)
                .Select(AdLdapMapper.ToGroup).ToList();

            // All names (light): what is missing is deleted, another DN is moved
            var allUsers = Names(baseline, UserFilter, cancel);
            var allGroups = Names(baseline, GroupFilter, cancel);
            var (g, gr) = AdDelta.Apply(previous, changedUsers, changedGroups, allUsers, allGroups);
            progress?.Report($"AD: {changedUsers.Count} gewijzigde gebruikers en {changedGroups.Count} gewijzigde groepen bijgewerkt ({g.Count} en {gr.Count} in totaal).");
            return new AdData(g, gr, domainDn, snapshot, Quick: true);
        }

        progress?.Report("AD: gebruikers lezen...");
        var users = Find(baseline, UserFilter, AdLdapMapper.UserAttributes, cancel)
            .Select(AdLdapMapper.ToUser).ToList();

        progress?.Report("AD: groepen lezen...");
        var groups = Find(baseline, GroupFilter, AdLdapMapper.GroupAttributes, cancel)
            .Select(AdLdapMapper.ToGroup).ToList();

        progress?.Report($"AD: {users.Count} gebruikers en {groups.Count} groepen gelezen.");
        return new AdData(users, groups, domainDn, snapshot);
    }, cancel);

    /// <summary>Which domain controller answers and up to where it is updated; null when that cannot be read.</summary>
    private AdSnapshot? ReadSnapshot()
    {
        try
        {
            using var root = Item("RootDSE");
            root.RefreshCache(new[] { "dsServiceName", "highestCommittedUSN" });
            return AdLdapMapper.ToSnapshot(root.Properties["dsServiceName"].Value, root.Properties["highestCommittedUSN"].Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Names(DirectoryEntry baseline, string filter, CancellationToken cancel)
        => Find(baseline, filter, new[] { "sAMAccountName", "distinguishedName" }, cancel)
            .Select(AdLdapMapper.ToUser)
            .Select(u => (Sam: u.SamAccountName, Dn: u.DistinguishedName))
            .Where(x => x.Sam.Length > 0)
            .GroupBy(x => x.Sam, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Dn, StringComparer.OrdinalIgnoreCase);

    public Task<AdUser?> ReadUserAsync(string samAccountName, CancellationToken cancel) => Task.Run(() =>
    {
        using var baseline = Item(ReadDomainDn());
        var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={AdLdapMapper.FilterValue(samAccountName)}))";
        return Find(baseline, filter, AdLdapMapper.UserAttributes, cancel).Select(AdLdapMapper.ToUser).FirstOrDefault();
    }, cancel);

    public Task<AdGroup?> ReadGroupAsync(string samAccountName, CancellationToken cancel) => Task.Run(() =>
    {
        using var baseline = Item(ReadDomainDn());
        var filter = $"(&(objectClass=group)(sAMAccountName={AdLdapMapper.FilterValue(samAccountName)}))";
        return Find(baseline, filter, AdLdapMapper.GroupAttributes, cancel).Select(AdLdapMapper.ToGroup).FirstOrDefault();
    }, cancel);

    private static IEnumerable<IReadOnlyDictionary<string, object?[]>> Find(DirectoryEntry baseline, string filter, string[] attributes, CancellationToken cancel)
    {
        using var searcher = new DirectorySearcher(baseline, filter, attributes, SearchScope.Subtree) { PageSize = 500 };
        using var results = searcher.FindAll();
        var output = new List<IReadOnlyDictionary<string, object?[]>>();
        foreach (SearchResult r in results)
        {
            cancel.ThrowIfCancellationRequested();
            var k = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in r.Properties.PropertyNames)
            {
                var values = r.Properties[name];
                var list = new object?[values.Count];
                values.CopyTo(list, 0);
                k[name] = list;
            }

            output.Add(k);
        }

        return output;
    }

    public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
        => Task.Run<IReadOnlyList<ChangeResult>>(() => changes.Select(w => Run(w, cancel)).ToList(), cancel);

    private ChangeResult Run(Change w, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return ChangeResult.Skip(w, "Geannuleerd.");
        }

        try
        {
            switch (w.ChangeAction)
            {
                case ChangeAction.AdAddGroupMember:
                {
                    using var groupValue = Item(w.P("groepDn"));
                    groupValue.Properties["member"].Add(w.Target);
                    groupValue.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdRemoveGroupMember:
                {
                    using var groupValue = Item(w.P("groepDn"));
                    groupValue.Properties["member"].Remove(w.Target);
                    groupValue.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdSetAttributes:
                {
                    using var item = Item(w.Target);
                    foreach (var (name, value) in w.Parameters)
                    {
                        if (string.IsNullOrEmpty(value))
                        {
                            item.Properties[name].Clear();
                        }
                        else
                        {
                            item.Properties[name].Value = value;
                        }
                    }

                    item.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdCreateUser:
                    return Create(w);

                case ChangeAction.AdCreateGroup:
                {
                    using var ou = Item(w.P("ou"));
                    using var groupValue = ou.Children.Add("CN=" + Core.Changes.PlanBuilder.EscapeCn(w.P("cn")), "group");
                    foreach (var (name, value) in w.Parameters)
                    {
                        if (name is "ou" or "cn" || string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        groupValue.Properties[name].Value = name == "groupType" ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value;
                    }

                    groupValue.CommitChanges();
                    return ChangeResult.Ok(w, "Aangemaakt.");
                }

                case ChangeAction.AdDisableAccount:
                {
                    using var item = Item(w.Target);
                    var uac = Convert.ToInt32(item.Properties["userAccountControl"].Value);
                    item.Properties["userAccountControl"].Value = uac | AdLdapMapper.UacDisabled;
                    item.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdEnableAccount:
                {
                    using var item = Item(w.Target);
                    var uac = Convert.ToInt32(item.Properties["userAccountControl"].Value);
                    item.Properties["userAccountControl"].Value = uac & ~AdLdapMapper.UacDisabled;
                    item.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdUnlockAccount:
                {
                    using var item = Item(w.Target);
                    item.Properties["lockoutTime"].Value = 0;
                    item.CommitChanges();
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.AdSetPassword:
                    return ConfigurePassword(w);

                case ChangeAction.AdMove:
                {
                    using var item = Item(w.Target);
                    using var target = Item(w.P("doelOu"));
                    item.MoveTo(target);
                    return ChangeResult.Ok(w);
                }

                default:
                    return ChangeResult.Error(w, $"Actie {w.ChangeAction} hoort niet bij AD.");
            }
        }
        catch (COMException ex) when (ex.HResult == AlreadyPresent && w.ChangeAction == ChangeAction.AdAddGroupMember)
        {
            return ChangeResult.Ok(w, "Was al lid.");
        }
        catch (Exception ex)
        {
            return ChangeResult.Error(w, ex.Message.Trim());
        }
    }

    /// <summary>New password (from Secret, never logged); optionally change at the next sign-in.</summary>
    private ChangeResult ConfigurePassword(Change w)
    {
        if (string.IsNullOrEmpty(w.Secret))
        {
            return ChangeResult.Error(w, "Geen wachtwoord meegegeven (het leeft enkel in het geheugen; maak het plan opnieuw).");
        }

        using var item = Item(w.Target);
        try
        {
            item.Invoke("SetPassword", w.Secret);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // E.g. the password does not meet the password policy of the domain
            return ChangeResult.Error(w, ex.InnerException.Message.Trim());
        }

        var change = string.Equals(w.P("wijzigenBijAanmelding"), "True", StringComparison.OrdinalIgnoreCase);
        item.Properties["pwdLastSet"].Value = change ? 0 : -1;
        item.CommitChanges();
        return ChangeResult.Ok(w, change ? "Ingesteld; wijzigen bij de volgende aanmelding." : "Ingesteld.");
    }

    private ChangeResult Create(Change w)
    {
        if (string.IsNullOrEmpty(w.Secret))
        {
            return ChangeResult.Error(w, "Geen startwachtwoord: het account is niet aangemaakt.");
        }

        using var ou = Item(w.P("ou"));
        using var user = ou.Children.Add("CN=" + Core.Changes.PlanBuilder.EscapeCn(w.P("cn")), "user");
        foreach (var (name, value) in w.Parameters)
        {
            if (name is "ou" or "cn" || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            user.Properties[name].Value = value;
        }

        user.CommitChanges();

        try
        {
            user.Invoke("SetPassword", w.Secret);
            user.Properties["userAccountControl"].Value = AdLdapMapper.UacNormalAccount;
            user.Properties["pwdLastSet"].Value = 0; // change the password at the first sign-in
            user.CommitChanges();
        }
        catch (Exception ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            return ChangeResult.Error(w, $"Account aangemaakt maar nog uitgeschakeld: wachtwoord instellen mislukt ({reason.Trim()}).");
        }

        return ChangeResult.Ok(w, "Aangemaakt en ingeschakeld.");
    }
}
