using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Common;

namespace Soforr.Application.Tests.Common;

public class HandlerRegistrationTests
{
    [Fact]
    public async Task AddHandlers_RegistersTheHandlerAsItsInterface_AndTheHandlerRuns()
    {
        using var provider = BuildProvider(typeof(EchoCommandHandler), typeof(CountQueryHandler));
        using var scope = provider.CreateScope();

        var command = scope.ServiceProvider.GetRequiredService<ICommandHandler<EchoCommand, string>>();
        var query = scope.ServiceProvider.GetRequiredService<IQueryHandler<CountQuery, int>>();

        Assert.Equal("hello", (await command.HandleAsync(new EchoCommand("hello"), CancellationToken.None)).Value);
        Assert.Equal(42, (await query.HandleAsync(new CountQuery(), CancellationToken.None)).Value);
    }

    [Fact]
    public void AddHandlers_AlsoRegistersTheConcreteHandler_SoBothResolveToTheSameInstanceInAScope()
    {
        using var provider = BuildProvider(typeof(EchoCommandHandler));
        using var scope = provider.CreateScope();

        var byInterface = scope.ServiceProvider.GetRequiredService<ICommandHandler<EchoCommand, string>>();
        var byClass = scope.ServiceProvider.GetRequiredService<EchoCommandHandler>();

        Assert.Same(byClass, byInterface);
    }

    [Fact]
    public void Handlers_AreScoped()
    {
        using var provider = BuildProvider(typeof(EchoCommandHandler));

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<EchoCommandHandler>(),
            first.ServiceProvider.GetRequiredService<EchoCommandHandler>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<EchoCommandHandler>(),
            second.ServiceProvider.GetRequiredService<EchoCommandHandler>());
    }

    [Fact]
    public async Task HandlersReceiveTheirDependenciesFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)));
        services.AddHandlers([typeof(StampCommandHandler)]);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<StampCommand, DateTimeOffset>>();
        var result = await handler.HandleAsync(new StampCommand(), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), result.Value);
    }

    [Fact]
    public void AddHandlers_IgnoresTypesThatAreNotConcreteHandlers()
    {
        var services = new ServiceCollection();

        services.AddHandlers([typeof(NotAHandler), typeof(AbstractHandler), typeof(EchoCommand), typeof(ICommandHandler<EchoCommand, string>)]);

        Assert.Empty(services);
    }

    [Fact]
    public void AddHandlers_RejectsTwoHandlersForTheSameCommand()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHandlers([typeof(EchoCommandHandler), typeof(DuplicateEchoCommandHandler)]));

        Assert.Contains(nameof(EchoCommand), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddHandlers_RegistersEveryHandlerInAnAssembly_WithoutConflicts()
    {
        var services = new ServiceCollection();

        var exception = Record.Exception(() => services.AddHandlers(typeof(DependencyInjection).Assembly));

        Assert.Null(exception);
    }

    private static ServiceProvider BuildProvider(params Type[] handlerTypes)
    {
        var services = new ServiceCollection();
        services.AddHandlers(handlerTypes);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public sealed record EchoCommand(string Text);

    public sealed record CountQuery;

    public sealed record StampCommand;

    public sealed class EchoCommandHandler : ICommandHandler<EchoCommand, string>
    {
        public Task<Result<string>> HandleAsync(EchoCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(command.Text));
    }

    public sealed class DuplicateEchoCommandHandler : ICommandHandler<EchoCommand, string>
    {
        public Task<Result<string>> HandleAsync(EchoCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(command.Text));
    }

    public sealed class CountQueryHandler : IQueryHandler<CountQuery, int>
    {
        public Task<Result<int>> HandleAsync(CountQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(42));
    }

    public sealed class StampCommandHandler(IClock clock) : ICommandHandler<StampCommand, DateTimeOffset>
    {
        public Task<Result<DateTimeOffset>> HandleAsync(StampCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(clock.UtcNow));
    }

    public sealed class NotAHandler;

    public abstract class AbstractHandler : ICommandHandler<EchoCommand, string>
    {
        public abstract Task<Result<string>> HandleAsync(EchoCommand command, CancellationToken cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
