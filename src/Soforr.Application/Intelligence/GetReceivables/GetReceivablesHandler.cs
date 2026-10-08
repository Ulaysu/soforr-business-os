using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence.Rules;
using Soforr.Application.Intelligence.Sources;
using Soforr.Application.Persistence;

namespace Soforr.Application.Intelligence.GetReceivables;

/// <summary>What customers owe, with the <see cref="Top"/> customers that are the most overdue (default <see cref="DefaultTop"/>, at most <see cref="MaxTop"/>); the totals cover every customer.</summary>
public sealed record GetReceivablesQuery(int? Top)
{
    public const int DefaultTop = 25;

    public const int MaxTop = 100;
}

/// <summary>An amount split into the aging buckets: days past the due date, in the organization's calendar.</summary>
public sealed record AgingBucketsDto(decimal Days0To30, decimal Days31To60, decimal Days61To90, decimal Days91Plus)
{
    internal static AgingBucketsDto From(AgingBuckets buckets) => new(buckets.Days0To30, buckets.Days31To60, buckets.Days61To90, buckets.Days91Plus);
}

/// <summary>One overdue sale: its balance, the due date fixed when it was recorded, the days past due and the bucket (<c>0-30</c>, <c>31-60</c>, <c>61-90</c> or <c>91+</c>).</summary>
public sealed record OverdueSaleDto(Guid SaleId, DateTimeOffset SoldAtUtc, DateTimeOffset DueAtUtc, decimal Total, decimal AmountPaid, decimal Outstanding, int DaysPastDue, string Bucket)
{
    internal static OverdueSaleDto From(OverdueSale sale) => new(
        sale.SaleId,
        sale.SoldAtUtc,
        sale.DueAtUtc,
        sale.Total,
        sale.AmountPaid,
        sale.Outstanding,
        sale.DaysPastDue,
        sale.Bucket switch
        {
            AgingBucket.Days0To30 => "0-30",
            AgingBucket.Days31To60 => "31-60",
            AgingBucket.Days61To90 => "61-90",
            _ => "91+",
        });
}

public sealed record CustomerReceivableDto(
    Guid CustomerId,
    string Name,
    decimal Outstanding,
    decimal NotYetDue,
    decimal Overdue,
    AgingBucketsDto Buckets,
    int OpenSaleCount,
    int OverdueSaleCount,
    int? OldestDaysPastDue,
    IReadOnlyList<OverdueSaleDto> OverdueSales)
{
    internal static CustomerReceivableDto From(CustomerReceivable customer) => new(
        customer.CustomerId,
        customer.Name,
        customer.Outstanding,
        customer.NotYetDue,
        customer.Overdue,
        AgingBucketsDto.From(customer.Buckets),
        customer.OpenSaleCount,
        customer.OverdueSaleCount,
        customer.OldestDaysPastDue,
        customer.OverdueSales.Select(OverdueSaleDto.From).ToList());
}

/// <summary>
/// What customers owe as of <see cref="AsOfUtc"/>. The totals cover every customer that owes something;
/// <see cref="Customers"/> lists the most overdue first. <see cref="MaterialityThreshold"/> is the organization's setting for the overdue
/// insights (null: none set), shown for context: this report lists every balance, whatever its size.
/// </summary>
public sealed record ReceivablesDto(
    DateTimeOffset AsOfUtc,
    string Currency,
    string TimeZoneId,
    decimal? MaterialityThreshold,
    decimal TotalOutstanding,
    decimal TotalNotYetDue,
    decimal TotalOverdue,
    AgingBucketsDto Buckets,
    int CustomersWithBalance,
    int CustomersOverdue,
    IReadOnlyList<CustomerReceivableDto> Customers);

/// <summary>
/// The receivables report (<see cref="ReceivablesCalculator"/>) for the caller's organization, computed on request from the completed
/// sales on account and their payments; nothing is stored. It needs <c>InsightsView</c>: it is the owner's and manager's view of money owed.
/// </summary>
public sealed class GetReceivablesHandler(IAppDbContext db, IClock clock, ICurrentUser user) : IQueryHandler<GetReceivablesQuery, ReceivablesDto>
{
    public async Task<Result<ReceivablesDto>> HandleAsync(GetReceivablesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InsightsView) is { } denied)
        {
            return Result.Failure<ReceivablesDto>(denied);
        }

        var top = query.Top ?? GetReceivablesQuery.DefaultTop;
        if (top is < 1 or > GetReceivablesQuery.MaxTop)
        {
            return Result.Failure<ReceivablesDto>(new Error("receivables.top_invalid", ErrorKind.Validation, $"The number of customers must be between 1 and {GetReceivablesQuery.MaxTop}.", "top"));
        }

        var input = await ReceivablesGatherer.GatherAsync(db, cancellationToken);
        var receivables = ReceivablesCalculator.Calculate(input.Sales, input.Customers, clock.UtcNow, input.Zone);

        return Result.Success(new ReceivablesDto(
            receivables.AsOfUtc,
            input.Currency,
            receivables.TimeZoneId,
            input.MaterialityThreshold,
            receivables.TotalOutstanding,
            receivables.TotalNotYetDue,
            receivables.TotalOverdue,
            AgingBucketsDto.From(receivables.Buckets),
            receivables.Customers.Count,
            receivables.Customers.Count(customer => customer.Overdue > 0m),
            receivables.Customers.Take(top).Select(CustomerReceivableDto.From).ToList()));
    }
}
