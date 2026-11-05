using System.Runtime.CompilerServices;

namespace Moss365.Tests;

internal static class TestInstellingen
{
    /// <summary>
    /// De bouwserver van GitHub (Windows) is trager dan een gewone pc: bUnit wacht standaard maar 1 seconde op een scherm
    /// dat na een achtergrondtaak opnieuw tekent. Een test die slaagt, wacht niet langer dan nodig.
    /// </summary>
    [ModuleInitializer]
    internal static void Instellen() => Bunit.TestContextBase.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}
