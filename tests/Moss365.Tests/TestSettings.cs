using System.Runtime.CompilerServices;

namespace Moss365.Tests;

internal static class TestSettings
{
    /// <summary>
    /// The GitHub build server (Windows) is slower than a normal PC: by default bUnit waits only 1 second for a screen
    /// that redraws after a background task. A test that passes does not wait longer than needed.
    /// </summary>
    [ModuleInitializer]
    internal static void Configure() => Bunit.TestContextBase.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}
