using System.Xml.Linq;
using Moss365.Core.Scheduling;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>R18 (U19): reading without a window, the task and what it can read.</summary>
public class UnattendedTests
{
    [Fact]
    public void TheTaskRunsDailyAtTheHourUnderTheSignedInUserOnly()
    {
        var xml = Unattended.TaskXml(@"C:\Users\an\AppData\Local\Moss365.Desktop\current\Moss & Co\Moss365.Desktop.exe", "a1b2-c3", 6, new DateTime(2026, 11, 26));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml, StringComparison.Ordinal);
        var task = XDocument.Parse(xml);
        string Value(string name) => task.Descendants().Single(e => e.Name.LocalName == name).Value;

        Assert.Equal("2026-11-26T06:00:00", Value("StartBoundary"));
        Assert.Equal("1", Value("DaysInterval"));
        Assert.Equal("InteractiveToken", Value("LogonType"));   // no password kept; DPAPI and the kept sign-in work
        Assert.Equal("LeastPrivilege", Value("RunLevel"));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.Equal("true", Value("StartWhenAvailable"));
        Assert.Equal("PT2H", Value("ExecutionTimeLimit"));
        Assert.Equal(@"C:\Users\an\AppData\Local\Moss365.Desktop\current\Moss & Co\Moss365.Desktop.exe", Value("Command"));
        Assert.Contains("&amp;", xml, StringComparison.Ordinal);
        Assert.Equal("--lezen a1b2-c3", Value("Arguments"));
        Assert.DoesNotContain(task.Descendants(), e => e.Name.LocalName is "UserId" or "Password");
        Assert.Equal(6, Unattended.HourOf(xml));
        Assert.Equal(23, Unattended.HourOf(Unattended.TaskXml("x.exe", "t", 30, DateTime.Today)));
        Assert.Null(Unattended.HourOf("geen xml"));
        Assert.Equal(@"\Moss365\Lezen a1b2-c3", Unattended.TaskName("a1b2-c3"));
    }

    [Fact]
    public void OnlyASafeTenantIdGoesOnTheCommandLine()
    {
        Assert.Equal("t-1", Unattended.TenantFrom(new[] { "--lezen", "t-1" }));
        Assert.Equal("t_1", Unattended.TenantFrom(new[] { "--LEZEN", "t_1" }));
        Assert.Null(Unattended.TenantFrom(new[] { "--lezen" }));
        Assert.Null(Unattended.TenantFrom(new[] { "--lezen", "t 1" }));
        Assert.Null(Unattended.TenantFrom(new[] { "--lezen", "t1\" & del" }));
        Assert.Null(Unattended.TenantFrom(new[] { "--veloapp-install", "1.0" }));
        Assert.Null(Unattended.TenantFrom(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => Unattended.Arguments("a;b"));
    }

    [Fact]
    public void WhatCanBeReadWithoutAWindow()
    {
        // A password is only typed in the app: nothing
        var locked = Unattended.Check(true, true, false, true, true, true, EncryptionMethod.Password);
        Assert.False(locked.CanRun);
        Assert.Contains(locked.Notes, n => n.Contains("wachtwoord", StringComparison.Ordinal));

        // AD with its own password and Microsoft 365 without "stay signed in": nothing either
        var nothing = Unattended.Check(true, true, true, true, true, false, EncryptionMethod.WindowsAccount);
        Assert.False(nothing.CanRun);
        Assert.Equal(3, nothing.Notes.Count);

        // AD with the Windows account; Microsoft 365 waits for the app
        var ad = Unattended.Check(true, true, false, true, true, false, EncryptionMethod.None);
        Assert.Equal((true, true, false), (ad.CanRun, ad.ReadsAd, ad.ReadsMicrosoft365));
        Assert.Contains(ad.Notes, n => n.Contains("Aangemeld blijven", StringComparison.Ordinal));

        // A cloud tenant that stays signed in, protected by an AD group
        var cloud = Unattended.Check(false, false, false, true, true, true, EncryptionMethod.AdGroup);
        Assert.Equal((true, false, true), (cloud.CanRun, cloud.ReadsAd, cloud.ReadsMicrosoft365));
        Assert.Empty(cloud.Notes);
    }
}
