using System.Text;
using Moss365.Core.LogonScripts;

namespace Moss365.Connections.Ad;

/// <summary>
/// The NETLOGON share (0.32): by default \\domain controller\NETLOGON, with the Windows account of this PC. The
/// policies are read from \\domain controller\SYSVOL. Writing only happens through a plan (AdLdap, AdWriteLogonScript).
/// </summary>
public sealed class NetlogonShare : ILogonScriptShare
{
    private static readonly string[] Extensions = { ".bat", ".cmd", ".vbs", ".ps1", ".kix" };
    private readonly string _server;

    public NetlogonShare(string server, string? path)
    {
        _server = server.Trim();
        Location = string.IsNullOrWhiteSpace(path) ? $@"\\{_server}\NETLOGON" : path.Trim().TrimEnd('\\');
    }

    public string Location { get; }

    public IReadOnlyList<LogonScriptFile> List()
        => Directory.EnumerateFiles(Location, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Select(f => new LogonScriptFile(Path.GetRelativePath(Location, f.FullName), f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string Read(string name) => Decode(File.ReadAllBytes(PathOf(name)));

    public IReadOnlyList<GpoScript> GpoScripts()
    {
        var sysvol = $@"\\{_server}\SYSVOL";
        var output = new List<GpoScript>();
        if (!Directory.Exists(sysvol))
        {
            return output;
        }

        foreach (var policies in Directory.EnumerateDirectories(sysvol).Select(d => Path.Combine(d, "Policies")).Where(Directory.Exists))
        {
            foreach (var policy in Directory.EnumerateDirectories(policies))
            {
                foreach (var ini in new[] { @"User\Scripts\scripts.ini", @"User\Scripts\psscripts.ini" }.Select(i => Path.Combine(policy, i)).Where(File.Exists))
                {
                    output.AddRange(ReadIni(Path.GetFileName(policy), Decode(File.ReadAllBytes(ini))));
                }
            }
        }

        return output;
    }

    /// <summary>Writes a new version; ASCII stays ASCII, otherwise the encoding the script had (UTF-8 with BOM for
    /// PowerShell, Windows-1252 for the rest).</summary>
    public void Write(string name, string content)
    {
        var path = PathOf(name);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var ascii = content.All(c => c < 128);
        var encoding = ascii ? Encoding.ASCII
            : Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase) ? new UTF8Encoding(true)
            : Encoding.Latin1;
        var temporary = path + ".moss365-tmp";
        File.WriteAllText(temporary, content, encoding);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathOf(string name)
    {
        var relative = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Location, relative));
        if (!full.StartsWith(Path.GetFullPath(Location).TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Het script ligt buiten NETLOGON.");
        }

        return full;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>scripts.ini: [Logon] 0CmdLine=x.bat, 0Parameters=...</summary>
    internal static IEnumerable<GpoScript> ReadIni(string policy, string text)
    {
        string? section = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<GpoScript>();
        void Flush()
        {
            if (section is null)
            {
                return;
            }

            foreach (var key in values.Keys.Where(k => k.EndsWith("CmdLine", StringComparison.OrdinalIgnoreCase)))
            {
                var index = key[..^"CmdLine".Length];
                output.Add(new GpoScript(policy, section, values[key], values.GetValueOrDefault(index + "Parameters") ?? string.Empty));
            }

            values.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1];
            }
            else if (line.IndexOf('=') is var eq and > 0)
            {
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        Flush();
        return output;
    }
}
