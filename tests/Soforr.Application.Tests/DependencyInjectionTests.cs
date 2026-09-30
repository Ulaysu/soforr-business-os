using Microsoft.Extensions.DependencyInjection;

namespace Soforr.Application.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_ReturnsSameServiceCollection_SoRegistrationCanBeChained()
    {
        var services = new ServiceCollection();

        var result = services.AddApplication();

        Assert.Same(services, result);
    }
}
