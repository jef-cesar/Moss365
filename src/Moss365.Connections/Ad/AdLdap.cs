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

    private readonly NetlogonShare? _netlogon;

    public AdLdap(string server, string? user, string? password, NetlogonShare? netlogon = null)
    {
        _netlogon = netlogon;
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

    /// <summary>BitLocker keys (msFVE-RecoveryInformation under the computer) and the LAPS password (Windows LAPS in
    /// clear text, or the legacy ms-Mcs-AdmPwd) of a computer (0.33). Only called when the access log records it.</summary>
    public Task<Core.Helpdesk.AdComputerSecrets?> ComputerSecretsAsync(string computerName, CancellationToken cancel) => Task.Run(() =>
    {
        using var baseline = Item(ReadDomainDn());
        var name = AdLdapMapper.FilterValue(computerName.Trim().TrimEnd('$'));
        var computer = Find(baseline, $"(&(objectCategory=computer)(|(cn={name})(sAMAccountName={name}$)))",
            new[] { "cn", "distinguishedName", "ms-Mcs-AdmPwd", "ms-Mcs-AdmPwdExpirationTime", "msLAPS-Password", "msLAPS-EncryptedPassword" }, cancel).FirstOrDefault();
        if (computer is null)
        {
            return null;
        }

        string? First(string attribute) => computer.TryGetValue(attribute, out var v) && v.Length > 0 ? v[0]?.ToString() : null;
        Core.Helpdesk.LapsPassword? laps = null;
        string? note = null;
        if (First("msLAPS-Password") is { } json)
        {
            // Windows LAPS in clear text: {"n":"account","t":"time","p":"password"}
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            laps = new Core.Helpdesk.LapsPassword(root.TryGetProperty("n", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("p", out var pw) ? pw.GetString() : null, null, "Windows LAPS in AD");
        }
        else if (First("ms-Mcs-AdmPwd") is { } legacy)
        {
            laps = new Core.Helpdesk.LapsPassword("Administrator", legacy, null, "LAPS (oud) in AD");
        }
        else if (computer.ContainsKey("msLAPS-EncryptedPassword"))
        {
            note = "Het LAPS-wachtwoord staat versleuteld in AD: bekijk het met de LAPS-tools van Windows.";
        }

        var dn = First("distinguishedName") ?? string.Empty;
        var keys = new List<Core.Helpdesk.BitLockerKey>();
        if (dn.Length > 0)
        {
            using var item = Item(dn);
            foreach (var k in Find(item, "(objectClass=msFVE-RecoveryInformation)", new[] { "msFVE-RecoveryPassword", "msFVE-RecoveryGuid", "whenCreated" }, cancel))
            {
                var guid = k.TryGetValue("msFVE-RecoveryGuid", out var g) && g.Length > 0 && g[0] is byte[] bytes ? new Guid(bytes).ToString() : string.Empty;
                var created = k.TryGetValue("whenCreated", out var w) && w.Length > 0 && w[0] is DateTime t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : (DateTimeOffset?)null;
                keys.Add(new Core.Helpdesk.BitLockerKey(guid, dn, k.TryGetValue("msFVE-RecoveryPassword", out var r) && r.Length > 0 ? r[0]?.ToString() : null, created, "AD"));
            }
        }

        return new Core.Helpdesk.AdComputerSecrets(First("cn") ?? computerName, keys, laps, note);
    }, cancel);

    /// <summary>The light AD security check (0.34): read only, a few LDAP queries.</summary>
    public Task<Core.Security.AdSecurityReading?> ReadSecurityAsync(CancellationToken cancel) => Task.Run<Core.Security.AdSecurityReading?>(() =>
    {
        var domainDn = ReadDomainDn();
        using var baseline = Item(domainDn);
        List<string> Names(string filter) => Find(baseline, filter, new[] { "sAMAccountName", "cn" }, cancel)
            .Select(r => (r.TryGetValue("sAMAccountName", out var v) && v.Length > 0 ? v[0]?.ToString() : null)
                         ?? (r.TryGetValue("cn", out var c) && c.Length > 0 ? c[0]?.ToString() : null) ?? string.Empty)
            .Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        const string bit = "userAccountControl:1.2.840.113556.1.4.803:";
        var spn = Names("(&(objectCategory=person)(objectClass=user)(servicePrincipalName=*)(!(sAMAccountName=krbtgt)))");
        var delegation = Names($"(&(|(objectCategory=person)(objectCategory=computer))({bit}=524288)(!(primaryGroupID=516)))");
        var notRequired = Names($"(&(objectCategory=person)(objectClass=user)({bit}=32)(!({bit}=2)))");
        var reversible = Names($"(&(objectCategory=person)(objectClass=user)({bit}=128))");
        DateTimeOffset? krbtgt = Find(baseline, "(&(objectClass=user)(sAMAccountName=krbtgt))", new[] { "pwdLastSet" }, cancel)
            .Select(r => r.TryGetValue("pwdLastSet", out var v) && v.Length > 0 && v[0] is long ft && ft > 0 ? DateTimeOffset.FromFileTime(ft) : (DateTimeOffset?)null)
            .FirstOrDefault();

        // adminCount=1 while no longer in a privileged group (the rights of AdminSDHolder stay on the account)
        var privileged = Find(baseline, "(&(objectClass=group)(adminCount=1))", new[] { "distinguishedName" }, cancel)
            .Select(r => r.TryGetValue("distinguishedName", out var v) && v.Length > 0 ? v[0]?.ToString() : null).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = Find(baseline, "(&(objectCategory=person)(objectClass=user)(adminCount=1))", new[] { "sAMAccountName", "memberOf" }, cancel)
            .Where(r => !(r.TryGetValue("memberOf", out var m) && m.OfType<object>().Select(x => x.ToString()).Any(x => x is not null && privileged.Contains(x))))
            .Select(r => r.TryGetValue("sAMAccountName", out var v) && v.Length > 0 ? v[0]?.ToString() ?? string.Empty : string.Empty)
            .Where(n => n.Length > 0 && !n.Equals("krbtgt", StringComparison.OrdinalIgnoreCase)).ToList();

        var old = Find(baseline, "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(|(operatingSystem=*XP*)(operatingSystem=*Vista*)(operatingSystem=*Windows 7*)(operatingSystem=*Windows 8*)(operatingSystem=*2003*)(operatingSystem=*2008*)(operatingSystem=*2012*)))",
                new[] { "cn", "operatingSystem" }, cancel)
            .Select(r => (Name: r.TryGetValue("cn", out var c) && c.Length > 0 ? c[0]?.ToString() ?? string.Empty : string.Empty,
                          Os: r.TryGetValue("operatingSystem", out var o) && o.Length > 0 ? o[0]?.ToString() ?? string.Empty : string.Empty))
            .ToList();
        return new Core.Security.AdSecurityReading(spn, delegation, notRequired, reversible, krbtgt, orphans, old);
    }, cancel);

    /// <summary>Group Policies (groupPolicyContainer) and where they are linked (gPLink), read only (0.34).</summary>
    public Task<IReadOnlyList<Core.Security.GpoInfo>?> ReadGpoAsync(CancellationToken cancel) => Task.Run<IReadOnlyList<Core.Security.GpoInfo>?>(() =>
    {
        var domainDn = ReadDomainDn();
        using var baseline = Item(domainDn);
        var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Find(baseline, "(gPLink=*)", new[] { "distinguishedName", "gPLink" }, cancel))
        {
            var dn = r.TryGetValue("distinguishedName", out var d) && d.Length > 0 ? d[0]?.ToString() ?? string.Empty : string.Empty;
            var text = r.TryGetValue("gPLink", out var l) && l.Length > 0 ? l[0]?.ToString() ?? string.Empty : string.Empty;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"cn=(\{[0-9a-fA-F-]+\})", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var id = m.Groups[1].Value;
                if (!links.TryGetValue(id, out var list))
                {
                    links[id] = list = new List<string>();
                }

                list.Add(dn);
            }
        }

        using var policies = Item($"CN=Policies,CN=System,{domainDn}");
        return Find(policies, "(objectClass=groupPolicyContainer)", new[] { "cn", "displayName", "flags", "versionNumber", "whenChanged" }, cancel)
            .Select(r =>
            {
                string First(string a) => r.TryGetValue(a, out var v) && v.Length > 0 ? v[0]?.ToString() ?? string.Empty : string.Empty;
                var id = First("cn");
                var flags = int.TryParse(First("flags"), out var f) ? f : 0;
                var changed = r.TryGetValue("whenChanged", out var w) && w.Length > 0 && w[0] is DateTime t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : (DateTimeOffset?)null;
                return new Core.Security.GpoInfo(id, First("displayName"), (flags & 1) != 0, (flags & 2) != 0, int.TryParse(First("versionNumber"), out var vn) ? vn : 0,
                    links.GetValueOrDefault(id) ?? new List<string>(), changed);
            })
            .ToList();
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

                case ChangeAction.AdWriteLogonScript:
                {
                    if (_netlogon is null)
                    {
                        return ChangeResult.Error(w, "NETLOGON is niet bereikbaar.");
                    }

                    _netlogon.Write(w.Target, w.P("inhoud"));
                    return ChangeResult.Ok(w, $"Bewaard in {_netlogon.Location}");
                }

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
