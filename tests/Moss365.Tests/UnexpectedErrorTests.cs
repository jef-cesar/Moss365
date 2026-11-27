using System.Reflection;
using Moss365.Core.Localization;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>The window for an error that nobody caught: 0.39.0 showed an empty window for an error without a message.</summary>
public class UnexpectedErrorTests
{
    private sealed class Silent : Exception
    {
        public Silent() : base(string.Empty)
        {
        }
    }

    private static Exception Thrown(Exception error)
    {
        try
        {
            throw error;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void An_error_without_a_message_is_never_an_empty_window()
    {
        using var _ = Texts.Scope("en");
        var text = UnexpectedError.Describe(Thrown(new Silent()));

        Assert.StartsWith("The error has no description.", text);
        Assert.Contains("Kind: " + typeof(Silent).FullName, text);
        Assert.Contains("Where:", text);
        Assert.Contains(nameof(Thrown), text);
        Assert.Contains("Ctrl+C", text);
    }

    [Fact]
    public void The_message_of_an_inner_error_is_used_when_the_outer_one_has_none()
    {
        using var _ = Texts.Scope("nl");
        var text = UnexpectedError.Describe(Thrown(new InvalidOperationException(" ", new IOException("Het bestand is in gebruik."))));

        Assert.StartsWith("Het bestand is in gebruik.", text);
        Assert.Contains("Soort: System.InvalidOperationException > System.IO.IOException", text);
    }

    [Fact]
    public void Wrappers_of_reflection_and_tasks_are_left_out()
    {
        using var _ = Texts.Scope("en");
        var wrapped = new TargetInvocationException(new AggregateException(Thrown(new KeyNotFoundException("No such key."))));

        var text = UnexpectedError.Describe(wrapped);

        Assert.StartsWith("No such key.", text);
        Assert.Contains("Kind: System.Collections.Generic.KeyNotFoundException", text);
        Assert.DoesNotContain("TargetInvocationException", text);
    }

    [Fact]
    public void The_technical_log_gets_the_kind_and_the_place_but_not_the_message()
    {
        var line = UnexpectedError.ForLog(Thrown(new InvalidOperationException("Pieter Voorbeeld heeft geen postvak")));

        Assert.Contains("System.InvalidOperationException", line);
        Assert.Contains(nameof(Thrown), line);
        Assert.DoesNotContain("Voorbeeld", line);
    }

    [Fact]
    public void The_place_is_a_few_lines_of_method_names()
    {
        using var _ = Texts.Scope("en");
        var text = UnexpectedError.Describe(Thrown(new InvalidOperationException("deep")));
        var place = text.Split(Environment.NewLine).SkipWhile(l => l != "Where:").Skip(1).TakeWhile(l => l.Length > 0).ToList();

        Assert.InRange(place.Count, 1, UnexpectedError.Frames);
        Assert.All(place, l => Assert.StartsWith("at ", l));
    }
}
