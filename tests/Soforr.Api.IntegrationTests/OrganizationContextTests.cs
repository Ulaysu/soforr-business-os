using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Organizations;

namespace Soforr.Api.IntegrationTests;

public class OrganizationContextTests
{
    [Fact]
    public void WithoutAuthentication_TheOrganizationContextHasNoOrganization_SoConsumersFailClosed()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IOrganizationContext>();

        Assert.Null(context.OrganizationId);
    }
}
