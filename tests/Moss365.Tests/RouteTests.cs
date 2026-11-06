using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Moss365.Tests;

/// <summary>Every parameter in the address of a page (e.g. <c>/gebruiker/{Key}</c>) is a [Parameter] of that page: the
/// router binds them by name, and a name that does not match leaves the page empty.</summary>
public sealed class RouteTests
{
    [Fact]
    public void EveryRouteParameterIsAParameterOfThePage()
    {
        var pages = typeof(Moss365.UI.Services.ManagementState).Assembly.GetTypes()
            .Select(t => (Type: t, Routes: t.GetCustomAttributes<RouteAttribute>().ToList()))
            .Where(x => x.Routes.Count > 0)
            .ToList();
        Assert.NotEmpty(pages);
        foreach (var (type, routes) in pages)
        {
            var parameters = type.GetProperties().Where(p => p.GetCustomAttribute<ParameterAttribute>() is not null)
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in routes.SelectMany(r => Regex.Matches(r.Template, @"\{(\w+)").Select(m => m.Groups[1].Value)))
            {
                Assert.True(parameters.Contains(name), $"{type.Name}: {{{name}}} in the route is not a [Parameter]");
            }
        }
    }
}
