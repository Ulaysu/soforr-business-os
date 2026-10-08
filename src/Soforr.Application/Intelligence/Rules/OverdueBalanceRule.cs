using System.Globalization;

namespace Soforr.Application.Intelligence.Rules;

/// <summary>
/// <b>Overdue customer balance.</b> A pure rule over <see cref="Receivables"/> and the organization's materiality threshold.
/// <para>
/// <b>When.</b> A customer with an overdue amount greater than zero and <b>at least the organization's threshold</b> gets one insight.
/// The threshold is set by the owner (<c>Organization.OverdueMaterialityThreshold</c>, in the organization's currency); it is never
/// assumed: when it is not set (null) there is no minimum and every overdue balance is reported.
/// </para>
/// <para>
/// <b>Severity.</b> <c>High</c> when any of the overdue amount is 91 or more days past due; <c>Medium</c> when the oldest is 31 to 90 days
/// past due; <c>Low</c> when everything overdue is 0 to 30 days past due.
/// </para>
/// <para>
/// <b>Traceable.</b> The facts give the amount overdue and outstanding, how it splits across the aging buckets, the oldest age in days,
/// the threshold used and the date and time zone of the evaluation, and name the sales behind it (the oldest <see cref="ListedSales"/>,
/// each with its balance, due date and days past due). The result is ordered by severity (high first), then the amount overdue, then name, then id.
/// </para>
/// </summary>
public static class OverdueBalanceRule
{
    public const string Type = "customers.overdue_balance";

    public const string CollectAction = "collect_payment";

    /// <summary>How many of a customer's overdue sales are named in an insight, oldest first.</summary>
    public const int ListedSales = 5;

    public static IReadOnlyList<Insight> Evaluate(Receivables receivables, decimal? materialityThreshold)
    {
        ArgumentNullException.ThrowIfNull(receivables);

        return receivables.Customers
            .Where(customer => customer.Overdue > 0m && (materialityThreshold is not { } threshold || customer.Overdue >= threshold))
            .Select(customer => (Customer: customer, Severity: SeverityOf(customer)))
            .OrderByDescending(item => item.Severity)
            .ThenByDescending(item => item.Customer.Overdue)
            .ThenBy(item => item.Customer.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Customer.CustomerId)
            .Select(item => ToInsight(item.Customer, item.Severity, receivables, materialityThreshold))
            .ToList();
    }

    public static InsightSeverity SeverityOf(CustomerReceivable customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return customer.Buckets.Days91Plus > 0m ? InsightSeverity.High
            : customer.Buckets.Days31To60 + customer.Buckets.Days61To90 > 0m ? InsightSeverity.Medium
            : InsightSeverity.Low;
    }

    private static Insight ToInsight(CustomerReceivable customer, InsightSeverity severity, Receivables receivables, decimal? threshold)
    {
        var facts = new List<InsightFact>
        {
            InsightFact.Number("overdue_amount", customer.Overdue),
            InsightFact.Number("outstanding_amount", customer.Outstanding),
            InsightFact.Number("not_yet_due_amount", customer.NotYetDue),
            InsightFact.Number("overdue_sales", customer.OverdueSaleCount),
            InsightFact.Number("oldest_days_past_due", customer.OldestDaysPastDue!.Value, "days"),
            InsightFact.Number("overdue_0_30_days", customer.Buckets.Days0To30),
            InsightFact.Number("overdue_31_60_days", customer.Buckets.Days31To60),
            InsightFact.Number("overdue_61_90_days", customer.Buckets.Days61To90),
            InsightFact.Number("overdue_91_plus_days", customer.Buckets.Days91Plus),
            threshold is { } set ? InsightFact.Number("materiality_threshold", set) : InsightFact.Text("materiality_threshold", "none set"),
            InsightFact.Text("as_of", $"{DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receivables.AsOfUtc, TimeZoneInfo.FindSystemTimeZoneById(receivables.TimeZoneId)).DateTime):yyyy-MM-dd} ({receivables.TimeZoneId})"),
        };

        var listed = customer.OverdueSales.Take(ListedSales).ToList();
        for (var index = 0; index < listed.Count; index++)
        {
            var sale = listed[index];
            facts.Add(InsightFact.Text(
                $"overdue_sale_{index + 1}",
                string.Create(CultureInfo.InvariantCulture, $"{sale.SaleId:D} | owes {Format(sale.Outstanding)} | due {sale.DueAtUtc:yyyy-MM-dd} | {sale.DaysPastDue} days past due")));
        }

        if (customer.OverdueSaleCount > listed.Count)
        {
            facts.Add(InsightFact.Number("overdue_sales_not_listed", customer.OverdueSaleCount - listed.Count));
        }

        var sales = customer.OverdueSaleCount == 1 ? "1 sale" : $"{customer.OverdueSaleCount} sales";
        var reason = $"{customer.Name} has {Format(customer.Overdue)} overdue on {sales}, the oldest {customer.OldestDaysPastDue} days past due; {Format(customer.Outstanding)} is outstanding in all.";

        return Insight.Create(
            Type,
            severity,
            new InsightSubject("Customer", customer.CustomerId, customer.Name),
            facts,
            reason,
            new InsightAction(CollectAction, $"Collect from {customer.Name}"));
    }

    private static string Format(decimal value) => InsightFact.Number("x", value).Value;
}
