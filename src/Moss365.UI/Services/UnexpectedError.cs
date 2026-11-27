using System.Reflection;
using Moss365.Core.Localization;

namespace Moss365.UI.Services;

/// <summary>
/// The text of the window for an error that nobody caught. Never empty: 0.39.0 showed only the message of the error,
/// and an error without a message gave an empty window. The text names the kind of error and where it happened (the
/// methods, never data), so that a copy (Ctrl+C in the window) says enough to find the cause.
/// </summary>
public static class UnexpectedError
{
    /// <summary>How many lines of the place are shown.</summary>
    public const int Frames = 8;

    public static string Describe(Exception error)
    {
        var inner = Unwrap(error);
        var message = Messages(inner).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim()
                      ?? Texts.T["desktop.error.no-message"];
        var lines = new List<string>
        {
            message,
            string.Empty,
            Texts.T.Format("desktop.error.kind", string.Join(" > ", Chain(inner).Select(e => e.GetType().FullName))),
        };
        var place = Place(inner);
        if (place.Count > 0)
        {
            lines.Add(Texts.T["desktop.error.place"]);
            lines.AddRange(place);
        }

        lines.Add(string.Empty);
        lines.Add(Texts.T["desktop.error.copy"]);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>For the technical log: the kind and the place, without the message (it can hold a name).</summary>
    public static string ForLog(Exception error)
    {
        var inner = Unwrap(error);
        return string.Join(" > ", Chain(inner).Select(e => e.GetType().FullName)) + Environment.NewLine + string.Join(Environment.NewLine, Place(inner));
    }

    /// <summary>The error that matters: not the wrapper of a call through reflection or of one task.</summary>
    public static Exception Unwrap(Exception error)
    {
        while (true)
        {
            switch (error)
            {
                case TargetInvocationException { InnerException: { } i }:
                    error = i;
                    continue;
                case AggregateException a when a.InnerExceptions.Count == 1:
                    error = a.InnerExceptions[0];
                    continue;
                default:
                    return error;
            }
        }
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    private static IEnumerable<string> Messages(Exception error) => Chain(error).Select(e => e.Message);

    /// <summary>The first lines of the stack (method names only), of the innermost error that has one.</summary>
    private static List<string> Place(Exception error)
    {
        var withStack = Chain(error).LastOrDefault(e => !string.IsNullOrWhiteSpace(e.StackTrace)) ?? error;
        return (withStack.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(Frames)
            .ToList();
    }
}
