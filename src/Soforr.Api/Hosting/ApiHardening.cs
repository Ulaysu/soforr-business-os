using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Soforr.Api.Hosting;

/// <summary>
/// The limits and allowlists of the HTTP surface (SECURITY-002), bound from the "Hardening" configuration section. Every value has a
/// safe default; a value that makes no sense stops the application at start instead of silently weakening it.
/// </summary>
public sealed class ApiHardeningOptions
{
    public const string SectionName = "Hardening";

    /// <summary>Requests per client address per minute across the whole API (health checks excluded).</summary>
    public int GlobalPermitsPerMinute { get; set; } = 600;

    /// <summary>Requests per client address per minute to the authentication endpoints (register, sign in, sign out): the brute-force limit.</summary>
    public int AuthPermitsPerMinute { get; set; } = 10;

    /// <summary>The largest request body accepted, in bytes. Bigger requests are answered 413.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Browser origins (scheme, host and optional port, no path) that may call the API from another site. Empty by default: the Web
    /// application is served from the same site (ADR 0002) and needs no CORS. A wildcard is never accepted.
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    /// <summary>The HSTS max-age in days (not sent in Development or over plain HTTP).</summary>
    public int HstsDays { get; set; } = 365;
}

internal sealed class ApiHardeningOptionsValidator : IValidateOptions<ApiHardeningOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiHardeningOptions options)
    {
        var failures = new List<string>();

        if (options.GlobalPermitsPerMinute < 1)
        {
            failures.Add("Hardening:GlobalPermitsPerMinute must be at least 1.");
        }

        if (options.AuthPermitsPerMinute < 1)
        {
            failures.Add("Hardening:AuthPermitsPerMinute must be at least 1.");
        }

        if (options.MaxRequestBodyBytes < 1)
        {
            failures.Add("Hardening:MaxRequestBodyBytes must be at least 1.");
        }

        if (options.HstsDays < 1)
        {
            failures.Add("Hardening:HstsDays must be at least 1.");
        }

        foreach (var origin in options.CorsAllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.AbsolutePath != "/"
                || origin.EndsWith('/')
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                failures.Add($"Hardening:CorsAllowedOrigins contains '{origin}', which is not an origin (scheme://host[:port], no path, no wildcard).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Rate limiting, request size limit, security headers, HSTS and the CORS allowlist. Contains no business logic.</summary>
public static class ApiHardeningExtensions
{
    public const string AuthPolicy = "auth";

    private const string CorsPolicy = "soforr-allowlist";

    public static IServiceCollection AddApiHardening(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<ApiHardeningOptions>, ApiHardeningOptionsValidator>();
        services.AddOptions<ApiHardeningOptions>().Bind(configuration.GetSection(ApiHardeningOptions.SectionName)).ValidateOnStart();

        // The options are read when the services are first used (not while they are registered), so configuration added by the host is honored.
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<ApiHardeningOptions>>((cors, hardening) => cors.AddPolicy(CorsPolicy, policy =>
        {
            var origins = hardening.Value.CorsAllowedOrigins;
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins)
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    .WithHeaders("Content-Type", "X-CSRF-TOKEN")
                    .AllowCredentials()
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            }
        }));

        services.AddHsts(hsts =>
        {
            hsts.IncludeSubDomains = true;
            hsts.MaxAge = TimeSpan.FromDays(configuration.GetSection(ApiHardeningOptions.SectionName).Get<ApiHardeningOptions>()?.HstsDays ?? new ApiHardeningOptions().HstsDays);
        });

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ApiHardeningOptions>>((limiter, hardening) =>
        {
            var settings = hardening.Value;
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Health checks are for the platform, not for a client, and must keep answering under load.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                    ? RateLimitPartition.GetNoLimiter("health")
                    : FixedPerMinute("global:" + ClientOf(context), settings.GlobalPermitsPerMinute));

            limiter.AddPolicy(AuthPolicy, context => FixedPerMinute("auth:" + ClientOf(context), settings.AuthPermitsPerMinute));

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests.",
                        Detail = "Too many requests. Wait and try again.",
                        Extensions = { ["code"] = "rate_limit.exceeded" },
                    },
                });
            };
        });

        return services;
    }

    /// <summary>
    /// Security headers on every response (also on errors and 429), and the request size limit. The size is checked from the declared
    /// length, and set on the server for a body sent in chunks, so an oversized body is refused before it is read.
    /// </summary>
    internal static IApplicationBuilder UseApiHardening(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetService<IOptions<ApiHardeningOptions>>()?.Value ?? new ApiHardeningOptions();

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

                // The API serves data, not pages: nothing may be framed, scripted or embedded, and nothing private may be cached.
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    headers["Cache-Control"] = "no-store";
                }

                return Task.CompletedTask;
            });

            if (context.Request.ContentLength > options.MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                var problems = context.RequestServices.GetService<IProblemDetailsService>();
                if (problems is not null)
                {
                    await problems.TryWriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context,
                        ProblemDetails =
                        {
                            Status = StatusCodes.Status413PayloadTooLarge,
                            Title = "The request is too large.",
                            Detail = string.Create(CultureInfo.InvariantCulture, $"A request can be at most {options.MaxRequestBodyBytes} bytes."),
                            Extensions = { ["code"] = "request.too_large" },
                        },
                    });
                }

                return;
            }

            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = options.MaxRequestBodyBytes;
            }

            await next();
        });
    }

    /// <summary>HSTS outside Development (the header is only ever sent over HTTPS), the CORS allowlist and the rate limiter. After routing, before authentication.</summary>
    public static WebApplication UseApiProtections(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        // With no allowed origin the policy is empty: a cross-origin browser request gets no CORS headers and is blocked by the browser.
        app.UseCors(CorsPolicy);
        app.UseRateLimiter();
        return app;
    }

    private static RateLimitPartition<string> FixedPerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static string ClientOf(HttpContext context) => context.Connection.RemoteIpAddress is { } address ? address.ToString() : "unknown";
}
