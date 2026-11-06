using System.Net.NetworkInformation;
using Moss365.Core.Setup;

namespace Moss365.Connections.Check;

/// <summary>The domain of this PC and the domain controller that signed it in (logon server), without RSAT.</summary>
public static class LocalDomainFinder
{
    public static LocalDomain? Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string domain;
        try
        {
            domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch (NetworkInformationException)
        {
            domain = string.Empty;
        }

        return From(domain, Environment.GetEnvironmentVariable("USERDNSDOMAIN"), Environment.GetEnvironmentVariable("LOGONSERVER"), Environment.MachineName);
    }

    /// <summary>A workgroup PC has no domain; a logon server that is the PC itself is a local sign-in.</summary>
    internal static LocalDomain? From(string? machineDomain, string? userDomain, string? logonServer, string machine)
    {
        var domain = (string.IsNullOrWhiteSpace(machineDomain) ? userDomain : machineDomain)?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(domain) || !domain.Contains('.'))
        {
            return null;
        }

        var server = logonServer?.Trim().TrimStart('\\');
        var controller = string.IsNullOrEmpty(server) || string.Equals(server, machine, StringComparison.OrdinalIgnoreCase)
            ? null
            : (server.Contains('.') ? server : $"{server}.{domain}").ToLowerInvariant();
        return new LocalDomain(domain, controller);
    }
}
