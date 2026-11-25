using Moss365.Connections.Ad;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.32: the NETLOGON share (here a folder on disk) and the scripts of Group Policies.</summary>
public sealed class NetlogonShareTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-netlogon-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void ScriptsAreListedReadAndWrittenInsideTheShareOnly()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "dienst"));
        File.WriteAllText(Path.Combine(_folder, "logon.bat"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(_folder, "dienst", "x.cmd"), "rem x\r\n");
        File.WriteAllText(Path.Combine(_folder, "readme.txt"), "not a script");
        var share = new NetlogonShare("dc01", _folder);

        var names = share.List().Select(f => f.Name.Replace('\\', '/')).ToList();
        Assert.Equal(new[] { "dienst/x.cmd", "logon.bat" }, names);
        Assert.Equal("@echo off\r\n", share.Read("logon.bat"));

        share.Write("logon.bat", "@echo off\r\nnet use H: \\\\fs01\\home\r\n");
        Assert.Contains("net use H:", share.Read("logon.bat"), StringComparison.Ordinal);
        share.Write("nieuw.ps1", "Write-Host 'cafeé'\r\n");
        Assert.Equal("Write-Host 'cafeé'\r\n", share.Read("nieuw.ps1"));
        Assert.Throws<InvalidOperationException>(() => share.Write("..\\buiten.bat", "x"));
        Assert.Equal("\\\\dc01\\NETLOGON", new NetlogonShare("dc01", null).Location);
    }

    [Fact]
    public void ScriptsIniOfAPolicyIsRead()
    {
        var scripts = NetlogonShare.ReadIni("{GUID}", "\r\n[Logon]\r\n0CmdLine=map.bat\r\n0Parameters=/q\r\n1CmdLine=\\\\fs01\\x.vbs\r\n1Parameters=\r\n[Logoff]\r\n0CmdLine=bye.cmd\r\n0Parameters=\r\n").ToList();
        Assert.Equal(3, scripts.Count);
        Assert.Contains(scripts, s => s.Kind == "Logon" && s.Script == "map.bat" && s.Parameters == "/q");
        Assert.Contains(scripts, s => s.Kind == "Logoff" && s.Script == "bye.cmd");
    }
}
