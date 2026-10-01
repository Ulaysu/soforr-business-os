using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure.Identity;

internal static class IdentityServiceCollectionExtensions
{
    /// <summary>The cookie name uses the <c>__Host-</c> prefix: browsers then accept it only if it is Secure and scoped to the whole host.</summary>
    public const string SessionCookieName = "__Host-soforr.session";

    public static IServiceCollection AddIdentityAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpContextAccessor();

        // Roles are not Identity roles: a user's role is the Domain's Membership (ADR 0002), so no role store is added.
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Length over composition rules (ADR 0002, current NIST guidance).
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.User.RequireUniqueEmail = true;

                // The user name is the e-mail address; do not restrict its characters beyond e-mail validation.
                options.User.AllowedUserNameCharacters = string.Empty;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<MembershipClaimsPrincipalFactory>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        // A revoked or changed session (password change, role change, deactivation) must die quickly (ADR 0002).
        services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<IOptions<SessionOptions>>((options, session) => options.ValidationInterval = session.Value.SecurityStampValidationInterval);

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<SessionOptions>>(ConfigureSessionCookie);

        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<HttpContextCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<HttpContextCurrentUser>());
        services.AddScoped<IOrganizationContext>(provider => provider.GetRequiredService<HttpContextCurrentUser>());

        return services;
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options, IOptions<SessionOptions> session)
    {
        options.Cookie.Name = SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = session.Value.IdleTimeout;
        options.SlidingExpiration = true;

        // This is an API: an unauthenticated or forbidden request is answered with the status code, never redirected.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        var securityStampValidation = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            var now = context.HttpContext.RequestServices.GetRequiredService<IClock>().UtcNow;
            context.Properties.Items.TryGetValue(SessionPolicy.SessionStartKey, out var sessionStart);

            if (SessionPolicy.IsExpired(sessionStart, now, session.Value.AbsoluteLifetime))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                return;
            }

            await securityStampValidation(context);
        };
    }
}
