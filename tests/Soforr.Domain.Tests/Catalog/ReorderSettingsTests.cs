using Soforr.Domain.Catalog;
using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Catalog;

public class ReorderSettingsTests
{
    [Fact]
    public void Create_KeepsTheReorderPointAndLeadTime()
    {
        var settings = ReorderSettings.Create(Quantity.Create(25m), 14);

        Assert.Equal(Quantity.Create(25m), settings.ReorderPoint);
        Assert.Equal(14, settings.LeadTimeDays);
    }

    [Fact]
    public void Create_AcceptsAZeroReorderPointAndAZeroLeadTime()
    {
        var settings = ReorderSettings.Create(Quantity.Zero, 0);

        Assert.Equal(Quantity.Zero, settings.ReorderPoint);
        Assert.Equal(0, settings.LeadTimeDays);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_RejectsANegativeLeadTime(int leadTimeDays)
    {
        var exception = Assert.Throws<DomainException>(() => ReorderSettings.Create(Quantity.Create(5m), leadTimeDays));

        Assert.Equal("reorder_settings.lead_time_negative", exception.Code);
    }

    [Fact]
    public void Create_RequiresAReorderPoint()
    {
        Assert.Throws<ArgumentNullException>(() => ReorderSettings.Create(null!, 3));
    }

    [Fact]
    public void ReorderSettings_AreEqualByValue()
    {
        Assert.Equal(ReorderSettings.Create(Quantity.Create(5m), 7), ReorderSettings.Create(Quantity.Create(5.0m), 7));
        Assert.NotEqual(ReorderSettings.Create(Quantity.Create(5m), 7), ReorderSettings.Create(Quantity.Create(5m), 8));
    }
}
