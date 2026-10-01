namespace Soforr.Architecture.Tests;

/// <summary>
/// <c>IgnoreQueryFilters()</c> switches off tenant isolation for a query (ADR 0002). Production code may do it only
/// where there is no current organization yet, which is the sign-in lookup of the membership. Adding a file here is a
/// security decision for review.
/// </summary>
public class TenantFilterBypassTests
{
    private static readonly string[] Allowed =
    [
        "src/Soforr.Infrastructure/Identity/MembershipClaimsPrincipalFactory.cs",
        "src/Soforr.Infrastructure/Identity/SessionService.cs",
    ];

    [Fact]
    public void OnlyTheSignInLookups_BypassTheTenantQueryFilter()
    {
        var files = SolutionLayout.SourceFiles()
            .Where(file => file.Text.Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Allowed, files);
    }
}
