namespace Soforr.Application.Intelligence.Rules;

/// <summary>A completed sale on account: what was charged, what has been paid on it (payments minus reversals) and when it is due.</summary>
public sealed record ReceivableSale(Guid SaleId, Guid CustomerId, DateTimeOffset SoldAtUtc, DateTimeOffset DueAtUtc, decimal Total, decimal AmountPaid)
{
    public decimal Outstanding => Total - AmountPaid;
}

/// <summary>A customer as the calculator sees it.</summary>
public sealed record ReceivableCustomer(Guid CustomerId, string Name);

/// <summary>How far past its due date an overdue amount is, in whole days of the organization's calendar.</summary>
public enum AgingBucket
{
    Days0To30 = 1,
    Days31To60 = 2,
    Days61To90 = 3,
    Days91Plus = 4,
}

/// <summary>An amount split into the aging buckets.</summary>
public sealed record AgingBuckets(decimal Days0To30, decimal Days31To60, decimal Days61To90, decimal Days91Plus)
{
    public static AgingBuckets Empty { get; } = new(0m, 0m, 0m, 0m);

    public decimal Total => Days0To30 + Days31To60 + Days61To90 + Days91Plus;

    public AgingBuckets Add(AgingBucket bucket, decimal amount) => bucket switch
    {
        AgingBucket.Days0To30 => this with { Days0To30 = Days0To30 + amount },
        AgingBucket.Days31To60 => this with { Days31To60 = Days31To60 + amount },
        AgingBucket.Days61To90 => this with { Days61To90 = Days61To90 + amount },
        _ => this with { Days91Plus = Days91Plus + amount },
    };

    public AgingBuckets Plus(AgingBuckets other) =>
        new(Days0To30 + other.Days0To30, Days31To60 + other.Days31To60, Days61To90 + other.Days61To90, Days91Plus + other.Days91Plus);
}

/// <summary>One overdue sale: the part of it still owed, how long it has been overdue and the bucket that puts it in.</summary>
public sealed record OverdueSale(Guid SaleId, DateTimeOffset SoldAtUtc, DateTimeOffset DueAtUtc, decimal Total, decimal AmountPaid, decimal Outstanding, int DaysPastDue, AgingBucket Bucket);

/// <summary>
/// What one customer owes. <see cref="Outstanding"/> = <see cref="NotYetDue"/> + <see cref="Overdue"/>; <see cref="Buckets"/> splits
/// the overdue part. <see cref="OldestDaysPastDue"/> is null when nothing is overdue.
/// </summary>
public sealed record CustomerReceivable(
    Guid CustomerId,
    string Name,
    decimal Outstanding,
    decimal NotYetDue,
    decimal Overdue,
    AgingBuckets Buckets,
    int OpenSaleCount,
    int OverdueSaleCount,
    int? OldestDaysPastDue,
    IReadOnlyList<OverdueSale> OverdueSales);

/// <summary>What all customers owe, as of <see cref="AsOfUtc"/>, with the customers that owe something, the most overdue first.</summary>
public sealed record Receivables(
    DateTimeOffset AsOfUtc,
    string TimeZoneId,
    decimal TotalOutstanding,
    decimal TotalNotYetDue,
    decimal TotalOverdue,
    AgingBuckets Buckets,
    IReadOnlyList<CustomerReceivable> Customers);

/// <summary>
/// <b>Customer balance and aging.</b> A pure calculator: sales on account, customers, the evaluation time and the organization's
/// time zone in; balances and aging out; no I/O.
/// <para>
/// <b>Balance.</b> The balance of a sale is its total minus what was paid on it (payments minus reversals). <b>A payment reduces
/// the balance of the sale it was recorded against, and no other</b>: payments are recorded against a specific sale, so there is no
/// allocation order to choose (no oldest-first rule), and a reversal puts the amount back on that same sale. A customer's balance is the
/// sum of the balances of their completed sales on account; sales that owe nothing are left out.
/// </para>
/// <para>
/// <b>Due date and overdue.</b> The due date is the explicit <see cref="ReceivableSale.DueAtUtc"/> fixed when the sale was recorded (the
/// sale date plus the customer's credit terms then, or immediately when there were none); it never moves afterwards. A sale is
/// <i>overdue</i> when its due date is strictly before the evaluation time, the same rule as the customer balance query;
/// otherwise its balance is <i>not yet due</i>.
/// </para>
/// <para>
/// <b>Aging.</b> The days past due of an overdue sale are the whole calendar days, in the organization's time zone, between the day
/// it was due and the day of the evaluation (a sale that fell due earlier today is 0 days past due). Buckets: <c>0-30</c>, <c>31-60</c>,
/// <c>61-90</c> and <c>91+</c> days past due. Amounts are the balances of the sales, summed exactly (no rounding is involved).
/// </para>
/// <para>
/// Customers are ordered by the amount overdue (most first), then the amount outstanding, then name, then id; a customer's overdue sales
/// by days past due (oldest first), then amount, then id.
/// </para>
/// </summary>
public static class ReceivablesCalculator
{
    public const int Bucket1Last = 30;

    public const int Bucket2Last = 60;

    public const int Bucket3Last = 90;

    public static Receivables Calculate(
        IReadOnlyCollection<ReceivableSale> sales,
        IReadOnlyCollection<ReceivableCustomer> customers,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(zone);

        var today = LocalDay(nowUtc, zone);
        var names = customers.ToDictionary(customer => customer.CustomerId, customer => customer.Name);

        var receivables = sales
            .Where(sale => sale.Outstanding > 0m)
            .GroupBy(sale => sale.CustomerId)
            .Select(group => ForCustomer(group.Key, names.GetValueOrDefault(group.Key, string.Empty), group.ToList(), nowUtc, today, zone))
            .OrderByDescending(customer => customer.Overdue)
            .ThenByDescending(customer => customer.Outstanding)
            .ThenBy(customer => customer.Name, StringComparer.Ordinal)
            .ThenBy(customer => customer.CustomerId)
            .ToList();

        return new Receivables(
            nowUtc,
            zone.Id,
            receivables.Sum(customer => customer.Outstanding),
            receivables.Sum(customer => customer.NotYetDue),
            receivables.Sum(customer => customer.Overdue),
            receivables.Aggregate(AgingBuckets.Empty, (sum, customer) => sum.Plus(customer.Buckets)),
            receivables);
    }

    /// <summary>The bucket for a number of days past due.</summary>
    public static AgingBucket BucketFor(int daysPastDue) => daysPastDue switch
    {
        <= Bucket1Last => AgingBucket.Days0To30,
        <= Bucket2Last => AgingBucket.Days31To60,
        <= Bucket3Last => AgingBucket.Days61To90,
        _ => AgingBucket.Days91Plus,
    };

    private static CustomerReceivable ForCustomer(Guid customerId, string name, List<ReceivableSale> open, DateTimeOffset nowUtc, DateOnly today, TimeZoneInfo zone)
    {
        var overdue = new List<OverdueSale>();
        var notYetDue = 0m;
        var buckets = AgingBuckets.Empty;

        foreach (var sale in open)
        {
            if (sale.DueAtUtc >= nowUtc)
            {
                notYetDue += sale.Outstanding;
                continue;
            }

            var days = Math.Max(0, today.DayNumber - LocalDay(sale.DueAtUtc, zone).DayNumber);
            var bucket = BucketFor(days);
            buckets = buckets.Add(bucket, sale.Outstanding);
            overdue.Add(new OverdueSale(sale.SaleId, sale.SoldAtUtc, sale.DueAtUtc, sale.Total, sale.AmountPaid, sale.Outstanding, days, bucket));
        }

        var ordered = overdue
            .OrderByDescending(sale => sale.DaysPastDue)
            .ThenByDescending(sale => sale.Outstanding)
            .ThenBy(sale => sale.SaleId)
            .ToList();

        return new CustomerReceivable(
            customerId,
            name,
            open.Sum(sale => sale.Outstanding),
            notYetDue,
            buckets.Total,
            buckets,
            open.Count,
            ordered.Count,
            ordered.Count == 0 ? null : ordered[0].DaysPastDue,
            ordered);
    }

    private static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
