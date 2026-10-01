using System.Xml.Linq;

namespace Soforr.Architecture.Tests;

/// <summary>
/// Reads the references declared in the .csproj files under src/. Declared references are checked
/// in addition to compiled-assembly references because the compiler omits references that no code uses.
/// </summary>
internal static class SolutionLayout
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>The repository-relative paths (forward slashes) of every C# source file under src/, excluding build output and migrations.</summary>
    public static IEnumerable<(string RelativePath, string Text)> SourceFiles()
    {
        var src = Path.Combine(RepositoryRoot.Value, "src");

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/'))
            .Where(relative => !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/Migrations/", StringComparison.Ordinal))
            .Select(relative => (relative, File.ReadAllText(Path.Combine(RepositoryRoot.Value, relative))));
    }

    public static DeclaredReferences ForProject(string projectName)
    {
        var path = Path.Combine(RepositoryRoot.Value, "src", projectName, projectName + ".csproj");
        var project = XDocument.Load(path);

        return new DeclaredReferences(
            Projects: Includes(project, "ProjectReference").Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/'))).ToArray(),
            Packages: Includes(project, "PackageReference").ToArray(),
            Frameworks: Includes(project, "FrameworkReference").ToArray());
    }

    private static IEnumerable<string> Includes(XDocument project, string elementName) =>
        project.Descendants(elementName).Select(e => (string?)e.Attribute("Include")).OfType<string>();

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Soforr.BusinessOS.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate Soforr.BusinessOS.slnx above " + AppContext.BaseDirectory);
    }
}

internal sealed record DeclaredReferences(
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Frameworks);
