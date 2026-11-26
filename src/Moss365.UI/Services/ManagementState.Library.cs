using Moss365.Core.Localization;
using Moss365.Core.Changes;
using Moss365.Core.Scripts;

namespace Moss365.UI.Services;

/// <summary>
/// Update library (R8): what is new or changed in the script library of the repository, with the difference, and
/// downloading after confirmation. A script whose SHA-256 does not match the index is refused. A downloaded script
/// never runs by itself.
/// </summary>
public sealed partial class ManagementState
{
    private readonly Dictionary<string, byte[]> _downloaded = new(StringComparer.OrdinalIgnoreCase);
    private IScriptLibrarySource? _librarySource;

    /// <summary>Where the last check got the library from, or null.</summary>
    public string? LibraryWhere => _librarySource?.Where;

    /// <summary>Compares the index of the library with this tenant (nothing is downloaded or changed).</summary>
    public async Task<IReadOnlyList<LibraryChange>> CheckLibraryAsync(string? folder, CancellationToken cancel = default)
    {
        _librarySource = _factory?.ScriptLibrary(folder) ?? throw new InvalidOperationException(Texts.T["update-panel.updating-is-only-possible-in"]);
        _downloaded.Clear();
        var index = ScriptIndex.Parse(await _librarySource.IndexAsync(cancel));
        return LibraryUpdate.Compare(index, LibraryUpdate.Installed(ScriptFolders), AppVersion);
    }

    /// <summary>The difference between the script as this tenant has it and the new one (downloaded and checked, not stored).</summary>
    public async Task<IReadOnlyList<string>> LibraryDiffAsync(LibraryChange change, CancellationToken cancel = default)
    {
        var bytes = await DownloadCheckedAsync(change.Entry, cancel);
        var current = ReadScripts().FirstOrDefault(s => string.Equals((s.Folder.Length > 0 ? s.Folder + "/" : string.Empty) + s.FileName, change.Entry.Path, StringComparison.OrdinalIgnoreCase));
        return LibraryUpdate.Diff(current is null ? string.Empty : ScriptLibrary.Text(ScriptFolders, current), System.Text.Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Downloads and stores the chosen scripts, each checked against the index; the update goes to the change log.
    /// Returns what was refused.</summary>
    public async Task<IReadOnlyList<string>> TakeLibraryAsync(IReadOnlyList<LibraryChange> chosen, CancellationToken cancel = default)
    {
        if (IsHistoric)
        {
            throw new InvalidOperationException(Texts.T["code.management-state-library.you-are-looking-at-a"]);
        }

        var errors = new List<string>();
        var taken = new List<IndexEntry>();
        foreach (var c in chosen.Where(c => c.CanTake))
        {
            try
            {
                var bytes = await DownloadCheckedAsync(c.Entry, cancel);
                if (LibraryUpdate.Take(ScriptFolders, c.Entry, bytes) is { } error)
                {
                    errors.Add(error);
                }
                else
                {
                    taken.Add(c.Entry);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or ArgumentException)
            {
                errors.Add($"{c.Entry.Path}: {ex.Message}");
            }
        }

        if (taken.Count > 0 || errors.Count > 0)
        {
            ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, "Scriptbibliotheek bijwerken", "Moss365", "UpdateLibrary", LibraryWhere ?? string.Empty, "Scripts",
                $"Scriptbibliotheek bijgewerkt: {taken.Count} script(s)", taken.ToDictionary(e => e.Path, e => $"{e.Version} {e.Sha256}"),
                errors.Count == 0 ? nameof(ResultStatus.Succeeded) : nameof(ResultStatus.Failed), string.Join(" ", errors)));
            Log.Information("Scriptbibliotheek bijgewerkt: {Aantal} script(s), {Geweigerd} geweigerd", taken.Count, errors.Count);
        }

        Changed?.Invoke();
        return errors;
    }

    private async Task<byte[]> DownloadCheckedAsync(IndexEntry entry, CancellationToken cancel)
    {
        if (_downloaded.TryGetValue(entry.Path, out var known))
        {
            return known;
        }

        var bytes = await (_librarySource ?? throw new InvalidOperationException(Texts.T["code.management-state-library.check-the-library-first"])).FileAsync(entry.Path, cancel);
        if (!ScriptIndex.Matches(entry, bytes))
        {
            throw new InvalidDataException(Texts.T["code.management-state-library.the-fingerprint-does-not-match"]);
        }

        _downloaded[entry.Path] = bytes;
        return bytes;
    }

    public bool IsPinned(ScriptFile script) => ScriptLibrary.IsPinned(ScriptFolders, script);

    /// <summary>Pins a library script in this tenant (it stays on this version) or releases it.</summary>
    public void Pin(ScriptFile script, bool pinned)
    {
        ScriptLibrary.Pin(ScriptFolders, script, pinned);
        Access(pinned ? "Script vastgezet" : "Script losgemaakt", script.FileName);
        Changed?.Invoke();
    }
}
