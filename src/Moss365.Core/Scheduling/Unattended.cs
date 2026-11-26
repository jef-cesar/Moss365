using System.Globalization;
using System.Xml.Linq;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Scheduling;

/// <summary>Reading without a window for one tenant (R18, U19): on or off, and the hour (document
/// <see cref="Unattended.SettingsDocument"/>).</summary>
public sealed record UnattendedSettings(bool On, int Hour);

public enum UnattendedResult
{
    Done,
    DoneWithErrors,
    Skipped,
    Failed,
}

/// <summary>How the last read without a window went (document <see cref="Unattended.RunDocument"/>): per step its
/// name and, when it did not succeed, the reason.</summary>
public sealed record UnattendedRun(DateTimeOffset Start, DateTimeOffset End, UnattendedResult Result, IReadOnlyList<string> Notes);

/// <summary>Whether a tenant can be read without a window, and what is left out (AD with a password, Microsoft 365
/// without "stay signed in").</summary>
public sealed record UnattendedCheck(bool CanRun, bool ReadsAd, bool ReadsMicrosoft365, IReadOnlyList<string> Notes);

/// <summary>
/// Reading without a window (R18, U19): a task in the Windows Task Scheduler starts Moss365 at an hour you choose, with
/// <c>--lezen &lt;tenant&gt;</c>. It only reads: AD and Entra into a snapshot, and the sign-in and audit logs kept
/// locally. It never writes to AD, Entra or Exchange, and scheduled actions keep waiting for an administrator. It runs
/// under your own Windows account while you are signed in to Windows (the kept sign-in and a key protected by Windows
/// need that), and skips when Moss365 is open (the open app reads itself).
/// </summary>
public static class Unattended
{
    public const string Argument = "--lezen";
    public const string SettingsDocument = "lezen-zonder-venster";
    public const string RunDocument = "lezen-zonder-venster-laatste";

    /// <summary>The hour proposed when nothing is chosen yet: early, before the working day.</summary>
    public const int DefaultHour = 6;

    /// <summary>The longest a read may take; the task is stopped after that.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromHours(2);

    /// <summary>A tenant id goes on a command line: only letters, digits, hyphens and underscores.</summary>
    public static bool ValidTenantId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The name of the task: in the folder Moss365 of the Task Scheduler, one per tenant.</summary>
    public static string TaskName(string tenantId) => $@"\Moss365\Lezen {tenantId}";

    public static string Arguments(string tenantId)
        => ValidTenantId(tenantId) ? $"{Argument} {tenantId}" : throw new ArgumentException(Texts.T["unattended.invalid-tenant"], nameof(tenantId));

    /// <summary>The tenant id from the command line, or null when this start is not a read without a window.</summary>
    public static string? TenantFrom(IReadOnlyList<string> args)
        => args.Count >= 2 && string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase) && ValidTenantId(args[1]) ? args[1] : null;

    /// <summary>
    /// The task (Task Scheduler schema 1.2, the order of a task exported by Windows): every day at the hour; only while
    /// the user is signed in to Windows (an interactive token, no password kept); later when the PC was off at that
    /// hour; never twice at the same time; stopped after <see cref="Limit"/>; also on battery.
    /// </summary>
    public static string TaskXml(string exePath, string tenantId, int hour, DateTime today)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var start = today.Date.AddHours(Math.Clamp(hour, 0, 23));
        XElement E(string name, params object[] content) => new(ns + name, content);
        var task = E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", Texts.T["unattended.task-description"])),
            E("Triggers", E("CalendarTrigger",
                E("StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                E("Enabled", "true"),
                E("ScheduleByDay", E("DaysInterval", "1")))),
            E("Principals", E("Principal", new XAttribute("id", "Author"), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings",
                E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"),
                E("StartWhenAvailable", "true"),
                E("RunOnlyIfNetworkAvailable", "true"),
                E("AllowStartOnDemand", "true"),
                E("Enabled", "true"),
                E("Hidden", "false"),
                E("RunOnlyIfIdle", "false"),
                E("WakeToRun", "false"),
                E("ExecutionTimeLimit", $"PT{(int)Limit.TotalHours}H"),
                E("Priority", "7")),
            E("Actions", new XAttribute("Context", "Author"), E("Exec", E("Command", exePath), E("Arguments", Arguments(tenantId)))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    /// <summary>The hour of a task as Windows gives it back (schtasks /Query /XML), or null.</summary>
    public static int? HourOf(string? taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml))
        {
            return null;
        }

        try
        {
            var start = XDocument.Parse(taskXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "StartBoundary")?.Value;
            return DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.Hour : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>What can be read without a window, and why not the rest.</summary>
    public static UnattendedCheck Check(bool hasAd, bool adFilled, bool adNeedsPassword, bool hasMicrosoft365, bool graphFilled, bool staySignedIn, EncryptionMethod encryption)
    {
        var notes = new List<string>();
        if (encryption == EncryptionMethod.Password)
        {
            notes.Add(Texts.T["unattended.note.password"]);
            return new UnattendedCheck(false, false, false, notes);
        }

        var ad = hasAd && adFilled && !adNeedsPassword;
        if (hasAd && adFilled && adNeedsPassword)
        {
            notes.Add(Texts.T["unattended.note.ad-password"]);
        }
        else if (hasAd && !adFilled)
        {
            notes.Add(Texts.T["unattended.note.ad-not-set"]);
        }

        var m365 = hasMicrosoft365 && graphFilled && staySignedIn;
        if (hasMicrosoft365 && graphFilled && !staySignedIn)
        {
            notes.Add(Texts.T["unattended.note.stay-signed-in"]);
        }
        else if (hasMicrosoft365 && !graphFilled)
        {
            notes.Add(Texts.T["unattended.note.graph-not-set"]);
        }

        if (!ad && !m365)
        {
            notes.Add(Texts.T["unattended.note.nothing"]);
        }

        return new UnattendedCheck(ad || m365, ad, m365, notes);
    }
}
