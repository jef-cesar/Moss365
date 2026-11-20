using Moss365.Core.Scripts;
using Xunit;

namespace Moss365.Tests;

/// <summary>
/// R8: the script library in the repository. Every script is checked, and scripts/index.json must match the scripts.
/// After changing a script: run this test with MOSS365_WRITE_INDEX=1 to write the index again, and commit it.
/// </summary>
public sealed class ScriptLibraryRepoTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }

    private static string Scripts => Path.Combine(Root(), "scripts");

    [Fact]
    public void TheIndexMatchesTheScripts()
    {
        var expected = ScriptIndex.Build(Scripts).ToJson();
        var path = Path.Combine(Scripts, ScriptIndex.FileName);
        if (Environment.GetEnvironmentVariable("MOSS365_WRITE_INDEX") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.True(File.Exists(path), "scripts/index.json is missing: run this test with MOSS365_WRITE_INDEX=1.");
        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n"));
        Assert.NotEmpty(ScriptIndex.Parse(File.ReadAllText(path)).Scripts);
    }

    public static IEnumerable<object[]> Files() => Directory.EnumerateFiles(Scripts, "*.ps1", SearchOption.AllDirectories)
        .Select(f => new object[] { Path.GetRelativePath(Scripts, f).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryScriptIsFitForTheLibrary(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Scripts, relative));
        Assert.True(ScriptIndex.SafePath(relative), $"{relative}: use letters, digits, - and _ in folder and file names.");
        Assert.All(bytes, b => Assert.True(b < 128, $"{relative}: PowerShell in scripts/ is ASCII only."));

        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var header = ScriptText.Read(text, relative);
        Assert.True(header.FromBlock, $"{relative}: needs a <#MOSS365 ... #> header.");
        Assert.False(string.IsNullOrWhiteSpace(header.Version), $"{relative}: the header needs a Version.");
        Assert.DoesNotContain("Naam:", text[..Math.Min(text.Length, 400)], StringComparison.Ordinal);
        if (header.Writes)
        {
            Assert.Matches(@"SupportsShouldProcess\s*=\s*\$true", text);
        }

        Assert.Empty(ScriptText.Warnings(header, text));
    }
}
