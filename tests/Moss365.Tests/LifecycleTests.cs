using Moss365.Core.Devices;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17: the end of support comes from the life cycle data, per system and build.</summary>
public class LifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));

    [Theory]
    [InlineData("Windows 10 Enterprise", "10.0 (19045)", SupportStatus.Ended, "2025-10-14")]
    [InlineData("Windows 10 Enterprise LTSC", "10.0 (19044)", SupportStatus.EndingSoon, "2027-01-12")]
    [InlineData("Windows 10 Enterprise LTSC", "10.0 (17763)", SupportStatus.Supported, "2029-01-09")]
    [InlineData("Windows 11 Enterprise", "10.0 (22631)", SupportStatus.Ended, "2026-11-10")]
    [InlineData("Windows 11 Pro", "10.0 (26100)", SupportStatus.Supported, "2027-10-12")]
    [InlineData("Windows", "10.0.19045.5011", SupportStatus.Ended, "2025-10-14")]
    [InlineData("Windows", "10.0.26100.2314", SupportStatus.Supported, "2027-10-12")]
    [InlineData("Windows Server 2016 Standard", "10.0 (14393)", SupportStatus.EndingSoon, "2027-01-12")]
    [InlineData("Windows Server 2012 R2 Standard", "6.3 (9600)", SupportStatus.Ended, "2023-10-10")]
    [InlineData("Windows 7 Professional", "6.1 (7601)", SupportStatus.Ended, "2020-01-14")]
    public void TheEndOfSupportFollowsSystemAndBuild(string os, string version, SupportStatus status, string end)
    {
        var support = Lifecycle.Of(os, version, Now);
        Assert.Equal(status, support.Status);
        Assert.Equal(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), support.End);
    }

    [Theory]
    [InlineData("Windows", "10.0.19044.1288")] // Intune: Windows 10 21H2 or LTSC 2021, cannot be told
    [InlineData("Windows 11 Pro", "")]          // no build
    [InlineData("iOS", "17.5.1")]
    [InlineData("Windows 10 Enterprise LTSC", "10.0 (10240)")]
    public void WhatCannotBeToldStaysUnknown(string os, string version)
    {
        Assert.Equal(SupportStatus.Unknown, Lifecycle.Of(os, version, Now).Status);
    }

    [Fact]
    public void Windows10IsNoLongerSupportedSince14October2025()
    {
        Assert.True(DeviceAnalysis.Outdated("Windows 10 Pro 10.0 (19045)"));
        Assert.True(DeviceAnalysis.Outdated("Windows 7 Professional"));
        Assert.False(DeviceAnalysis.Outdated("Windows 11 Pro"));
    }
}
