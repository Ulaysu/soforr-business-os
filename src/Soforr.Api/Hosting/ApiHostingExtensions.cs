using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Formatting.Compact;
using Soforr.Api.Observability;

namespace Soforr.Api.Hosting;

/// <summary>
/// Cross-cutting HTTP concerns of the Api: logging, correlation IDs, and centralized error handling.
/// Contains no business logic.
/// </summary>
public static class ApiHostingExtensions
{
    private const string DevelopmentOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Configures Serilog. Levels and overrides come from the "Serilog" configuration section; the console sink is
    /// human-readable in Development and compact structured JSON elsewhere.
    /// </summary>
    public static IHostBuilder UseApiLogging(this IHostBuilder host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.UseSerilog(
            (context, services, configuration) =>
            {
                configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext();

                if (context.HostingEnvironment.IsDevelopment())
                {
                    configuration.WriteTo.Console(outputTemplate: DevelopmentOutputTemplate, formatProvider: CultureInfo.InvariantCulture);
                }
                else
                {
                    configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
                }
            },
            preserveStaticLogger: true);
    }

    /// <summary>
    /// Enums are written and read by name ("Owner"), not by number: names are the stable, readable API contract and
    /// reordering an enum must not change what clients see. Applies to every endpoint.
    /// </summary>
    public static IServiceCollection AddApiJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        return services;
    }

    /// <summary>
    /// Registers RFC 9457 problem details. Server errors are generic (no exception details) and every problem
    /// response carries the request's correlation ID.
    /// </summary>
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails(options => options.CustomizeProblemDetails = CustomizeProblemDetails);

        // A request the framework cannot read is the caller's mistake: answer with its 4xx status and do not log it
        // as an unhandled server error.
        services.AddExceptionHandler<ClientErrorExceptionHandler>();
        services.Configure<ExceptionHandlerOptions>(options =>
            options.SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException);

        return services;
    }

    /// <summary>
    /// Adds, in order: correlation ID, security headers and the request size limit, request logging, exception handling, and problem-details status pages.
    /// Must be placed before routing and endpoints.
    /// </summary>
    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseApiHardening();
        app.UseSerilogRequestLogging(options => options.Logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>());
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    private static void CustomizeProblemDetails(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;

        problem.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
        problem.Extensions.Remove("exception");

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            problem.Title = "An unexpected error occurred.";
            problem.Detail = null;
        }
    }
}
