using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Soforr.Api.Hosting;

namespace Soforr.Api.IntegrationTests;

/// <summary>
/// A minimal in-process host that uses the real Api pipeline (<c>UseApiPipeline</c>, <c>AddApiErrorHandling</c>)
/// with test-only endpoints, so cross-cutting behavior can be tested without the full application and without
/// a database. Log events are captured in memory.
/// </summary>
internal sealed class PipelineTestHost : IAsyncDisposable
{
    public const string SecretExceptionMessage = "super-secret-internal-detail";

    private readonly WebApplication _app;

    private PipelineTestHost(WebApplication app, HttpClient client, CapturingSink logs)
    {
        _app = app;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }

    public CapturingSink Logs { get; }

    public static async Task<PipelineTestHost> StartAsync(string environment)
    {
        var sink = new CapturingSink();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Host.UseSerilog(
            (_, configuration) => configuration.Enrich.FromLogContext().WriteTo.Sink(sink),
            preserveStaticLogger: true);
        builder.Services.AddApiErrorHandling();

        var app = builder.Build();
        app.UseApiPipeline();
        app.MapGet("/ok", () => "ok");
        app.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException(SecretExceptionMessage)));

        await app.StartAsync();
        return new PipelineTestHost(app, app.GetTestClient(), sink);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public static string? CorrelationIdOf(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        return logEvent.Properties.TryGetValue("CorrelationId", out var value) && value is ScalarValue scalar
            ? scalar.Value as string
            : null;
    }
}
