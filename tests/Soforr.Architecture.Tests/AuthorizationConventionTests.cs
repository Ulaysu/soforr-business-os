using System.Reflection;

namespace Soforr.Architecture.Tests;

/// <summary>
/// ADR 0002 and ADR 0003: every command and query handler begins with an explicit permission check through
/// <c>ICurrentUser</c>. A handler that does not even take the current user cannot check one, so this fails when a
/// new handler is written without it. Handlers that are meant to run before there is a signed-in user are listed
/// by name; adding to the list is a security decision for review.
/// </summary>
public class AuthorizationConventionTests
{
    private static readonly string[] HandlersWithoutACurrentUser =
    [
        // Run before anyone is signed in.
        "Soforr.Application.Identity.Register.RegisterHandler",
        "Soforr.Application.Identity.SignIn.SignInHandler",
    ];

    [Fact]
    public void EveryHandler_ExceptTheListedOnes_TakesTheCurrentUser()
    {
        var application = Assembly.Load("Soforr.Application");
        var currentUser = application.GetType("Soforr.Application.Identity.ICurrentUser");
        Assert.NotNull(currentUser);

        var handlers = application.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.GetInterfaces().Any(IsHandlerInterface))
            .ToArray();
        Assert.NotEmpty(handlers);

        var missing = handlers
            .Where(handler => !HandlersWithoutACurrentUser.Contains(handler.FullName!, StringComparer.Ordinal))
            .Where(handler => !handler.GetConstructors().Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == currentUser)))
            .Select(handler => handler.FullName!)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void TheListOfExceptions_NamesOnlyExistingHandlers()
    {
        var application = Assembly.Load("Soforr.Application");

        var unknown = HandlersWithoutACurrentUser.Where(name => application.GetType(name) is null).ToArray();

        Assert.Empty(unknown);
    }

    private static bool IsHandlerInterface(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition().FullName is "Soforr.Application.Common.ICommandHandler`2" or "Soforr.Application.Common.IQueryHandler`2";
}
