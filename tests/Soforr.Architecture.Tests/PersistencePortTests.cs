using System.Reflection;

namespace Soforr.Architecture.Tests;

/// <summary>
/// The Application persistence port (ADR-0001) may expose only provider-agnostic types, so handlers
/// cannot depend on PostgreSQL, Npgsql or relational-provider behavior through it.
/// </summary>
public class PersistencePortTests
{
    private static readonly string[] AllowedAssemblyNames =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Abstractions",
        "Soforr.Application",
        "Soforr.Domain",
    ];

    [Fact]
    public void IAppDbContext_ExposesOnlyProviderAgnosticTypes()
    {
        var port = Assembly.Load("Soforr.Application").GetType("Soforr.Application.Persistence.IAppDbContext");
        Assert.NotNull(port);

        var exposed = port.GetInterfaces().Append(port)
            .SelectMany(contract => contract.GetMethods())
            .SelectMany(method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType))
            .SelectMany(Flatten)
            .Select(type => type.Assembly.GetName().Name!)
            .Distinct()
            .ToArray();

        var violations = exposed.Where(name => !IsAllowed(name)).ToArray();
        Assert.Empty(violations);
    }

    private static bool IsAllowed(string assemblyName) =>
        assemblyName.StartsWith("System", StringComparison.Ordinal)
        || AllowedAssemblyNames.Contains(assemblyName, StringComparer.Ordinal);

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var inner in Flatten(argument))
            {
                yield return inner;
            }
        }
    }
}
