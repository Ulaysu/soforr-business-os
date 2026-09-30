using System.Reflection;

namespace Soforr.Architecture.Tests;

/// <summary>
/// Enforces the dependency model of ADR-0001:
/// Domain has no dependencies; Application -> Domain; Infrastructure -> Application + Domain;
/// Api -> Application + Infrastructure (composition only).
/// </summary>
public class DependencyDirectionTests
{
    private static readonly string[] BaseClassLibraryPrefixes = ["System", "mscorlib", "netstandard"];

    private static readonly string[] ApplicationForbiddenPrefixes =
    [
        "Soforr.Infrastructure",
        "Soforr.Api",
        "Npgsql",
        "Microsoft.AspNetCore",
    ];

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { "Soforr.Domain", [] },
        { "Soforr.Application", ["Soforr.Domain"] },
        { "Soforr.Infrastructure", ["Soforr.Application", "Soforr.Domain"] },
        { "Soforr.Api", ["Soforr.Application", "Soforr.Infrastructure"] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Project_ReferencesOnlyAllowedProjects(string project, string[] allowed)
    {
        var declared = SolutionLayout.ForProject(project);

        Assert.Empty(declared.Projects.Except(allowed));
    }

    [Fact]
    public void Domain_DeclaresNoPackageOrFrameworkReferences()
    {
        var declared = SolutionLayout.ForProject("Soforr.Domain");

        Assert.Empty(declared.Packages);
        Assert.Empty(declared.Frameworks);
    }

    [Fact]
    public void Domain_AssemblyReferencesOnlyTheBaseClassLibrary()
    {
        var referenced = ReferencedAssemblyNames("Soforr.Domain");

        var violations = referenced.Where(name => !StartsWithAny(name, BaseClassLibraryPrefixes)).ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void Application_DeclaresNoForbiddenPackageOrFrameworkReferences()
    {
        var declared = SolutionLayout.ForProject("Soforr.Application");

        var forbiddenPackages = declared.Packages.Where(name => StartsWithAny(name, ApplicationForbiddenPrefixes)).ToArray();
        var forbiddenFrameworks = declared.Frameworks.Where(name => StartsWithAny(name, ApplicationForbiddenPrefixes)).ToArray();

        Assert.Empty(forbiddenPackages);
        Assert.Empty(forbiddenFrameworks);
    }

    [Fact]
    public void Application_AssemblyDoesNotReferenceForbiddenAssemblies()
    {
        var referenced = ReferencedAssemblyNames("Soforr.Application");

        var violations = referenced.Where(name => StartsWithAny(name, ApplicationForbiddenPrefixes)).ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void Application_DeclaresOnlyTheProviderAgnosticEntityFrameworkCorePackage()
    {
        var declared = SolutionLayout.ForProject("Soforr.Application");

        var violations = declared.Packages.Where(IsProviderSpecificEntityFramework).ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void Application_AssemblyReferencesOnlyTheProviderAgnosticEntityFrameworkCore()
    {
        var referenced = ReferencedAssemblyNames("Soforr.Application");

        var violations = referenced.Where(IsProviderSpecificEntityFramework).ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void Infrastructure_AssemblyDoesNotReferenceApi()
    {
        var referenced = ReferencedAssemblyNames("Soforr.Infrastructure");

        Assert.DoesNotContain("Soforr.Api", referenced);
    }

    private static string[] ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    /// <summary>
    /// ADR-0001 allows Application to use Microsoft.EntityFrameworkCore only. Every other Microsoft.EntityFrameworkCore.*
    /// package (Relational, Design, SqlServer, Sqlite, ...) is provider-specific or tooling and belongs in Infrastructure.
    /// </summary>
    internal static bool IsProviderSpecificEntityFramework(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal)
        && !name.Equals("Microsoft.EntityFrameworkCore.Abstractions", StringComparison.Ordinal);

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
