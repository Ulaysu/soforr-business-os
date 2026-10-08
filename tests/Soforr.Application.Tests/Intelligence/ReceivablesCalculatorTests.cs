using Soforr.Application.Intelligence.Rules;

namespace Soforr.Application.Tests.Intelligence;

public class ReceivablesCalculatorTests
{
    // Noon UTC on 2026-06-01.
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Awa = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bakary = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly ReceivableCustomer[] Customers = [new(Awa, "Awa Jallow"), new(Bakary, "Bakary Ceesay")];

    private static ReceivableSale Sale(Guid customer, decimal total, decimal paid, DateTimeOffset due, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), customer, due.AddDays(-30), due, total, paid);

    private static Receivables Calculate(TimeZoneInfo? zone = null, DateTimeOffset? now = null, params ReceivableSale[] sales) =>
        ReceivablesCalculator.Calculate(sales, Customers, now ?? Now, zone ?? TimeZoneInfo.Utc);

    private static CustomerReceivable Single(params ReceivableSale[] sales) => Assert.Single(Calculate(null, null, sales).Customers);

    // ---- the balance

    [Fact]
    public void ACustomersBalance_IsTheSumOfWhatIsStillOwedOnTheirSales()
    {
        var customer = Single(
            Sale(Awa, 100m, 0m, Now.AddDays(10)),
            Sale(Awa, 80m, 30m, Now.AddDays(-5)),
            Sale(Awa, 50m, 50m, Now.AddDays(-5))); // settled: not a balance

        Assert.Equal((150m, 100m, 50m, 2, 1), (customer.Outstanding, customer.NotYetDue, customer.Overdue, customer.OpenSaleCount, customer.OverdueSaleCount));
    }

    [Fact]
    public void APayment_ReducesTheSaleItWasRecordedAgainst_AndNoOther()
    {
        // Two sales: the older one (due long ago) is untouched by a payment recorded against the newer one.
        var older = Sale(Awa, 100m, 0m, Now.AddDays(-100));
        var newer = Sale(Awa, 100m, 60m, Now.AddDays(-5));

        var customer = Single(older, newer);

        Assert.Equal(140m, customer.Outstanding);
        Assert.Equal((100m, 40m), (customer.Buckets.Days91Plus, customer.Buckets.Days0To30));
        Assert.Equal([100m, 40m], customer.OverdueSales.Select(s => s.Outstanding).ToArray());
    }

    [Fact]
    public void ASaleThatIsFullyPaid_IsNotAReceivable_AndACustomerWhoOwesNothingIsNotListed()
    {
        var receivables = Calculate(null, null, Sale(Awa, 100m, 100m, Now.AddDays(-50)), Sale(Bakary, 20m, 0m, Now.AddDays(3)));

        Assert.Equal(["Bakary Ceesay"], receivables.Customers.Select(c => c.Name).ToArray());
        Assert.Equal((20m, 20m, 0m), (receivables.TotalOutstanding, receivables.TotalNotYetDue, receivables.TotalOverdue));
    }

    // ---- overdue or not yet due

    [Fact]
    public void ASaleIsOverdue_OnlyWhenItsDueInstantIsStrictlyBeforeNow()
    {
        var justPassed = Single(Sale(Awa, 10m, 0m, Now.AddSeconds(-1)));
        var exactlyNow = Single(Sale(Awa, 10m, 0m, Now));
        var justAhead = Single(Sale(Awa, 10m, 0m, Now.AddSeconds(1)));

        Assert.Equal((10m, 0m), (justPassed.Overdue, justPassed.NotYetDue));
        Assert.Equal((0m, 10m), (exactlyNow.Overdue, exactlyNow.NotYetDue));
        Assert.Equal((0m, 10m), (justAhead.Overdue, justAhead.NotYetDue));
        Assert.Null(exactlyNow.OldestDaysPastDue);
    }

    [Fact]
    public void ASaleThatFellDueEarlierToday_IsOverdue_AtZeroDaysPastDue()
    {
        var customer = Single(Sale(Awa, 10m, 0m, Now.AddHours(-3)));

        var sale = Assert.Single(customer.OverdueSales);
        Assert.Equal((0, AgingBucket.Days0To30), (sale.DaysPastDue, sale.Bucket));
        Assert.Equal(0, customer.OldestDaysPastDue);
    }

    // ---- aging buckets

    [Theory]
    [InlineData(0, AgingBucket.Days0To30)]
    [InlineData(1, AgingBucket.Days0To30)]
    [InlineData(30, AgingBucket.Days0To30)]
    [InlineData(31, AgingBucket.Days31To60)]
    [InlineData(60, AgingBucket.Days31To60)]
    [InlineData(61, AgingBucket.Days61To90)]
    [InlineData(90, AgingBucket.Days61To90)]
    [InlineData(91, AgingBucket.Days91Plus)]
    [InlineData(400, AgingBucket.Days91Plus)]
    public void TheBuckets_HaveTheirDocumentedBoundaries(int daysPastDue, AgingBucket expected)
    {
        Assert.Equal(expected, ReceivablesCalculator.BucketFor(daysPastDue));
        var customer = Single(Sale(Awa, 10m, 0m, Now.AddDays(-daysPastDue).AddHours(-1)));

        var sale = Assert.Single(customer.OverdueSales);
        Assert.Equal((daysPastDue, expected), (sale.DaysPastDue, sale.Bucket));
    }

    [Fact]
    public void TheAmountsSplitAcrossTheBuckets_AndAddUpToTheOverdueAmount()
    {
        var customer = Single(
            Sale(Awa, 1m, 0m, Now.AddDays(-5)),
            Sale(Awa, 2m, 0m, Now.AddDays(-45)),
            Sale(Awa, 4m, 0m, Now.AddDays(-75)),
            Sale(Awa, 8m, 0m, Now.AddDays(-200)),
            Sale(Awa, 16m, 0m, Now.AddDays(20)));

        Assert.Equal(new AgingBuckets(1m, 2m, 4m, 8m), customer.Buckets);
        Assert.Equal((15m, 16m, 31m, 200), (customer.Overdue, customer.NotYetDue, customer.Outstanding, customer.OldestDaysPastDue));
        Assert.Equal(customer.Overdue, customer.Buckets.Total);
    }

    [Fact]
    public void DaysPastDue_AreWholeCalendarDaysInTheOrganizationsTimeZone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"); // UTC+9
        // Due 2026-05-01 16:00 UTC = 01:00 on May 2 in Tokyo. Now is 2026-06-01 12:00 UTC = 21:00 on June 1 in Tokyo.
        var due = new DateTimeOffset(2026, 5, 1, 16, 0, 0, TimeSpan.Zero);

        var inTokyo = Assert.Single(Single2(tokyo, Sale(Awa, 10m, 0m, due)).OverdueSales);
        var inUtc = Assert.Single(Single2(TimeZoneInfo.Utc, Sale(Awa, 10m, 0m, due)).OverdueSales);

        Assert.Equal(30, inTokyo.DaysPastDue); // May 2 -> June 1
        Assert.Equal(31, inUtc.DaysPastDue);   // May 1 -> June 1
        Assert.Equal(AgingBucket.Days0To30, inTokyo.Bucket);
        Assert.Equal(AgingBucket.Days31To60, inUtc.Bucket);
    }

    private static CustomerReceivable Single2(TimeZoneInfo zone, params ReceivableSale[] sales) => Assert.Single(Calculate(zone, null, sales).Customers);

    [Fact]
    public void JustAfterLocalMidnight_TheDayCountMovesOn()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var due = new DateTimeOffset(2026, 5, 1, 3, 0, 0, TimeSpan.Zero); // 12:00 on May 1 in Tokyo
        var beforeMidnight = new DateTimeOffset(2026, 5, 31, 14, 59, 59, TimeSpan.Zero); // 23:59:59 on May 31 in Tokyo
        var afterMidnight = new DateTimeOffset(2026, 5, 31, 15, 0, 0, TimeSpan.Zero);    // 00:00 on June 1 in Tokyo

        var before = Assert.Single(Calculate(tokyo, beforeMidnight, Sale(Awa, 10m, 0m, due)).Customers).OverdueSales[0];
        var after = Assert.Single(Calculate(tokyo, afterMidnight, Sale(Awa, 10m, 0m, due)).Customers).OverdueSales[0];

        Assert.Equal((30, 31), (before.DaysPastDue, after.DaysPastDue));
        Assert.Equal((AgingBucket.Days0To30, AgingBucket.Days31To60), (before.Bucket, after.Bucket));
    }

    // ---- totals and ordering

    [Fact]
    public void TheTotals_CoverEveryCustomer()
    {
        var receivables = Calculate(
            null,
            null,
            Sale(Awa, 100m, 0m, Now.AddDays(-100)),
            Sale(Awa, 50m, 0m, Now.AddDays(5)),
            Sale(Bakary, 30m, 10m, Now.AddDays(-10)));

        Assert.Equal((170m, 50m, 120m), (receivables.TotalOutstanding, receivables.TotalNotYetDue, receivables.TotalOverdue));
        Assert.Equal(new AgingBuckets(20m, 0m, 0m, 100m), receivables.Buckets);
        Assert.Equal(Now, receivables.AsOfUtc);
        Assert.Equal("UTC", receivables.TimeZoneId);
    }

    [Fact]
    public void Customers_AreOrderedByTheAmountOverdue_ThenOutstanding_ThenNameThenId()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var d = Guid.Parse("00000000-0000-0000-0000-00000000000d");
        var people = new ReceivableCustomer[] { new(a, "Zed"), new(b, "Amy"), new(c, "Amy"), new(d, "Notdue") };
        var sales = new[]
        {
            Sale(a, 50m, 0m, Now.AddDays(-2)),   // overdue 50
            Sale(b, 70m, 0m, Now.AddDays(-2)),   // overdue 70
            Sale(c, 70m, 0m, Now.AddDays(-2)),   // overdue 70, same name as b: id decides
            Sale(d, 999m, 0m, Now.AddDays(9)),   // nothing overdue, the largest balance
        };

        var names = ReceivablesCalculator.Calculate(sales, people, Now, TimeZoneInfo.Utc).Customers.Select(customer => (customer.Name, customer.CustomerId)).ToArray();

        Assert.Equal([("Amy", b), ("Amy", c), ("Zed", a), ("Notdue", d)], names);
    }

    [Fact]
    public void OverdueSales_AreOrderedOldestFirst_ThenByAmountThenId()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var customer = Single(
            Sale(Awa, 10m, 0m, Now.AddDays(-5)),
            Sale(Awa, 10m, 0m, Now.AddDays(-50)),
            Sale(Awa, 30m, 0m, Now.AddDays(-20), second),
            Sale(Awa, 30m, 0m, Now.AddDays(-20), first),
            Sale(Awa, 20m, 0m, Now.AddDays(-20)));

        Assert.Equal([50, 20, 20, 20, 5], customer.OverdueSales.Select(s => s.DaysPastDue).ToArray());
        Assert.Equal([30m, 30m, 20m], customer.OverdueSales.Skip(1).Take(3).Select(s => s.Outstanding).ToArray());
        Assert.Equal([first, second], customer.OverdueSales.Skip(1).Take(2).Select(s => s.SaleId).ToArray());
    }

    [Fact]
    public void TheSameInput_GivesTheSameResult_WhateverTheInputOrder()
    {
        var sales = Enumerable.Range(0, 20)
            .Select(i => Sale(i % 2 == 0 ? Awa : Bakary, 10m + i, i % 3 * 2m, Now.AddDays(i * 7 - 60), Guid.Parse($"00000000-0000-0000-0000-{i:D12}")))
            .ToArray();

        static string[] Describe(Receivables r) =>
        [
            r.TotalOutstanding + "/" + r.TotalOverdue + "/" + r.Buckets,
            .. r.Customers.Select(c => c.Name + ":" + c.Overdue + ":" + string.Join(",", c.OverdueSales.Select(s => s.SaleId + "@" + s.DaysPastDue))),
        ];

        var forward = Describe(ReceivablesCalculator.Calculate(sales, Customers, Now, TimeZoneInfo.Utc));
        var reversed = Describe(ReceivablesCalculator.Calculate(sales.Reverse().ToArray(), Customers.Reverse().ToArray(), Now, TimeZoneInfo.Utc));

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void WithNoSales_EverythingIsEmpty_NotAnError()
    {
        var receivables = Calculate();

        Assert.Empty(receivables.Customers);
        Assert.Equal((0m, 0m, 0m), (receivables.TotalOutstanding, receivables.TotalNotYetDue, receivables.TotalOverdue));
        Assert.Equal(AgingBuckets.Empty, receivables.Buckets);
    }
}
