using Moss365.Core.Scripts;

namespace Moss365.Connections.Scripts;

/// <summary>The script library of this repository on GitHub (the branch main), without signing in.</summary>
public sealed class GitHubScriptSource(HttpClient http, string repo = "jef-cesar/Moss365", string branch = "main") : IScriptLibrarySource
{
    private string Base => $"https://raw.githubusercontent.com/{repo}/{branch}/scripts/";

    public string Where => $"github.com/{repo} ({branch})";

    public async Task<string> IndexAsync(CancellationToken cancel) => await http.GetStringAsync(Base + ScriptIndex.FileName, cancel);

    public async Task<byte[]> FileAsync(string path, CancellationToken cancel)
    {
        if (!ScriptIndex.SafePath(path))
        {
            throw new ArgumentException("Geen geldig pad.", nameof(path));
        }

        return await http.GetByteArrayAsync(Base + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), cancel);
    }
}

/// <summary>The script library in a shared folder (the folder holds scripts\index.json, or index.json itself).</summary>
public sealed class FolderScriptSource(string folder) : IScriptLibrarySource
{
    private string Root => File.Exists(Path.Combine(folder, "scripts", ScriptIndex.FileName)) ? Path.Combine(folder, "scripts") : folder;

    public string Where => folder;

    public Task<string> IndexAsync(CancellationToken cancel) => File.ReadAllTextAsync(Path.Combine(Root, ScriptIndex.FileName), cancel);

    public Task<byte[]> FileAsync(string path, CancellationToken cancel)
    {
        if (!ScriptIndex.SafePath(path))
        {
            throw new ArgumentException("Geen geldig pad.", nameof(path));
        }

        return File.ReadAllBytesAsync(Path.Combine(new[] { Root }.Concat(path.Split('/')).ToArray()), cancel);
    }
}
