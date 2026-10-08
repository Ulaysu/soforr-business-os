using Soforr.Domain.Common;
using Soforr.Domain.Sales;

namespace Soforr.Domain.Tests.Sales;

public class SalePaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Seller = Guid.CreateVersion7();
    private static readonly Guid Customer = Guid.CreateVersion7();

    private static Money M(decimal amount) => Money.Create(amount, Gmd);

    private static SaleLineInput Line(decimal quantity = 10m, decimal price = 10m) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "SKU", "Rice", "bag", Quantity.Create(quantity), M(price), M(price), M(6m), null);

    /// <summary>A sale of 100 (10 x 10) on account, due 30 days later, optionally with an amount paid at the time.</summary>
    private static Sale OnAccount(decimal? paidAtSale = null, string? method = null, Guid? customer = null) =>
        Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [Line()], customer ?? Customer, Now.AddDays(30), paidAtSale is { } paid ? M(paid) : null, method);

    private static Sale WalkIn() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [Line()]);

    private static string CodeOf(Action action) => Assert.Throws<DomainException>(action).Code;

    private static SalePayment Pay(Sale sale, decimal amount, int daysAfter = 1, Guid? id = null, string? method = "Cash") =>
        sale.RecordPayment(id ?? Guid.CreateVersion7(), M(amount), Now.AddDays(daysAfter), method, Seller, Now.AddDays(daysAfter));

    // ---- settling at the time of the sale

    [Fact]
    public void TotalOf_IsTheTotalTheSaleWillHave_RoundingLineByLine()
    {
        var lines = new[]
        {
            new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "A", "A", "u", Quantity.Create(0.3333m), M(1.5m), M(1.5m), M(1m), null),
            new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "B", "B", "u", Quantity.Create(7.7777m), M(0.0003m), M(0.0003m), M(0m), null),
            new SaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), "C", "C", "u", Quantity.Create(2.5m), M(3.3333m), M(3.3333m), M(1m), null),
        };

        var predicted = Sale.TotalOf(Gmd, lines);

        Assert.Equal(Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, lines).Total, predicted);
        // Each line is rounded half away from zero to four places before summing: 0.49995 -> 0.5000, 0.00233331 -> 0.0023, 8.33325 -> 8.3333.
        Assert.Equal(0.5000m + 0.0023m + 8.3333m, predicted.Amount);
    }

    [Fact]
    public void ASaleOnAccount_KeepsItsCustomerAndFixedDueDate_AndOwesTheWholeTotalWhenNothingWasPaid()
    {
        var sale = OnAccount();

        Assert.Equal(Customer, sale.CustomerId);
        Assert.Equal(Now.AddDays(30), sale.DueAtUtc);
        Assert.Empty(sale.Payments);
        Assert.Equal(0m, sale.AmountPaid.Amount);
        Assert.Equal(100m, sale.Outstanding.Amount);
        Assert.Equal(0, sale.PaymentRevision);
    }

    [Fact]
    public void APaymentTakenWithTheSale_IsARecordWithTheSalesOwnId_AndReducesWhatIsOwed()
    {
        var sale = OnAccount(paidAtSale: 40m, method: "  Mobile money  ");

        var payment = Assert.Single(sale.Payments);
        Assert.Equal((sale.Id, SalePaymentKind.Payment, 40m, Now, "Mobile money"), (payment.Id, payment.Kind, payment.Amount.Amount, payment.PaidAtUtc, payment.MethodLabel));
        Assert.Equal((sale.Id, Organization, Seller, Now), (payment.SaleId, payment.OrganizationId, payment.RecordedByUserId, payment.RecordedAtUtc));
        Assert.Equal(40m, sale.AmountPaid.Amount);
        Assert.Equal(60m, sale.Outstanding.Amount);
    }

    [Fact]
    public void ASalePaidInFullAtTheTime_OwesNothing()
    {
        var sale = OnAccount(paidAtSale: 100m);

        Assert.Equal(0m, sale.Outstanding.Amount);
        Assert.Equal(100m, sale.AmountPaid.Amount);
    }

    [Fact]
    public void ZeroPaidAtTheTime_IsAFullyUnpaidSale_WithNoPaymentRecord()
    {
        var sale = OnAccount(paidAtSale: 0m);

        Assert.Empty(sale.Payments);
        Assert.Equal(100m, sale.Outstanding.Amount);
    }

    [Fact]
    public void PayingMoreThanTheTotalAtTheTime_IsRefused()
    {
        Assert.Equal("sale.payment_exceeds_total", CodeOf(() => OnAccount(paidAtSale: 100.0001m)));
        Assert.Equal("sale.payment_amount_negative", CodeOf(() => OnAccount(paidAtSale: -1m)));
    }

    [Fact]
    public void APaymentAtTheTimeInAnotherCurrency_IsRefused()
    {
        var line = Line();

        Assert.Equal("sale.payment_currency_mismatch", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Customer, Now, Money.Create(1m, Currency.Create("USD")))));
    }

    [Fact]
    public void AWalkInSale_IsPaidInFull_SoItHasNoDueDateNoPaymentAndNoBalance()
    {
        var sale = WalkIn();

        Assert.Null(sale.CustomerId);
        Assert.Null(sale.DueAtUtc);
        Assert.Empty(sale.Payments);
        Assert.Equal(0m, sale.Outstanding.Amount);
        Assert.Equal("sale.walk_in_is_paid_in_full", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [Line()], null, Now.AddDays(1))));
        Assert.Equal("sale.walk_in_is_paid_in_full", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [Line()], null, null, M(10m))));
    }

    [Fact]
    public void ADueDate_IsRequiredOnAccount_AndCannotBeBeforeTheSale()
    {
        var line = Line();

        Assert.Equal("sale.due_date_required", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Customer)));
        Assert.Equal("sale.due_before_sale", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Customer, Now.AddSeconds(-1))));
        Assert.Equal("sale.timestamp_not_utc", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Customer, new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.FromHours(1)))));
        Assert.Equal(Now, Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Customer, Now).DueAtUtc); // due immediately
    }

    [Fact]
    public void AnEmptyCustomerId_IsRefused_AndAMethodLabelIsLimited()
    {
        var line = Line();

        Assert.Equal("sale.customer_invalid", CodeOf(() => Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [line], Guid.Empty, Now)));
        Assert.Equal("sale.payment_method_too_long", CodeOf(() => OnAccount(paidAtSale: 10m, method: new string('x', SalePayment.MaxMethodLabelLength + 1))));
        Assert.Equal(SalePayment.MaxMethodLabelLength, OnAccount(paidAtSale: 10m, method: new string('x', SalePayment.MaxMethodLabelLength)).Payments[0].MethodLabel!.Length);
    }

    // ---- recording a payment

    [Fact]
    public void Payments_ReduceWhatIsOwed_UntilItIsPaid()
    {
        var sale = OnAccount();

        var first = Pay(sale, 30m);
        Pay(sale, 50m, daysAfter: 2);

        Assert.Equal((SalePaymentKind.Payment, 30m), (first.Kind, first.Amount.Amount));
        Assert.Equal(80m, sale.AmountPaid.Amount);
        Assert.Equal(20m, sale.Outstanding.Amount);
        Pay(sale, 20m, daysAfter: 3);
        Assert.Equal(0m, sale.Outstanding.Amount);
        Assert.Equal(3, sale.PaymentRevision);
    }

    [Fact]
    public void APaymentMoreThanIsOwed_IsRefused_AndChangesNothing()
    {
        var sale = OnAccount(paidAtSale: 40m);

        Assert.Equal("sale.payment_exceeds_outstanding", CodeOf(() => Pay(sale, 60.0001m)));

        Assert.Single(sale.Payments);
        Assert.Equal(60m, sale.Outstanding.Amount);
        Assert.Equal(0, sale.PaymentRevision);
        Pay(sale, 60m); // exactly what is owed is fine
        Assert.Equal(0m, sale.Outstanding.Amount);
    }

    [Fact]
    public void APaymentOnASaleThatIsPaidInFull_IsRefused()
    {
        var sale = OnAccount(paidAtSale: 100m);

        Assert.Equal("sale.payment_exceeds_outstanding", CodeOf(() => Pay(sale, 0.0001m)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void APaymentMustBeGreaterThanZero(double amount)
    {
        var sale = OnAccount();

        Assert.Equal("sale.payment_amount_required", CodeOf(() => Pay(sale, (decimal)amount)));
        Assert.Empty(sale.Payments);
    }

    [Fact]
    public void APaymentInAnotherCurrency_IsRefused()
    {
        var sale = OnAccount();

        Assert.Equal("sale.payment_currency_mismatch", CodeOf(() => sale.RecordPayment(Guid.CreateVersion7(), Money.Create(10m, Currency.Create("USD")), Now.AddDays(1), "Cash", Seller, Now.AddDays(1))));
    }

    [Fact]
    public void APaymentDate_CannotBeBeforeTheSale_OrInTheFuture_ButMayBeEarlierThanWhenItIsRecorded()
    {
        var sale = OnAccount();

        Assert.Equal("sale.payment_before_sale", CodeOf(() => sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now.AddSeconds(-1), "Cash", Seller, Now.AddDays(1))));
        Assert.Equal("sale.payment_in_future", CodeOf(() => sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now.AddDays(2), "Cash", Seller, Now.AddDays(1))));
        Assert.Equal("sale.timestamp_not_utc", CodeOf(() => sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now.AddDays(1), "Cash", Seller, new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.FromHours(1)))));

        var payment = sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now.AddDays(1), "Cash", Seller, Now.AddDays(5)); // received on day 1, recorded on day 5
        Assert.Equal((Now.AddDays(1), Now.AddDays(5)), (payment.PaidAtUtc, payment.RecordedAtUtc));
        sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now, "Cash", Seller, Now); // the moment of the sale itself is allowed
    }

    [Fact]
    public void APaymentNeedsAnIdAUserAndAUniqueId()
    {
        var sale = OnAccount();
        var id = Guid.CreateVersion7();
        Pay(sale, 10m, id: id);

        Assert.Equal("sale.payment_id_required", CodeOf(() => Pay(sale, 10m, id: Guid.Empty)));
        Assert.Equal("sale.payment_id_duplicate", CodeOf(() => Pay(sale, 10m, id: id)));
        Assert.Equal("sale.payment_user_required", CodeOf(() => sale.RecordPayment(Guid.CreateVersion7(), M(10m), Now.AddDays(1), "Cash", Guid.Empty, Now.AddDays(1))));
        Assert.Single(sale.Payments);
    }

    [Fact]
    public void AMethodLabel_IsTrimmedOptionalAndLimited()
    {
        var sale = OnAccount();

        Assert.Equal("Cash", Pay(sale, 1m, method: "  Cash ").MethodLabel);
        Assert.Null(Pay(sale, 1m, method: "   ").MethodLabel);
        Assert.Null(Pay(sale, 1m, method: null).MethodLabel);
        Assert.Equal("sale.payment_method_too_long", CodeOf(() => Pay(sale, 1m, method: new string('x', 51))));
    }

    [Fact]
    public void AWalkInSale_AndAVoidedSale_CannotBePaid()
    {
        var walkIn = WalkIn();
        var voided = OnAccount();
        voided.Void(Guid.CreateVersion7(), "Mistake", Now.AddMinutes(1));

        Assert.Equal("sale.not_payable", CodeOf(() => Pay(walkIn, 10m)));
        Assert.Equal("sale.not_payable", CodeOf(() => Pay(voided, 10m)));
    }

    // ---- reversing a payment

    [Fact]
    public void AReversal_IsANewRecordThatPutsTheAmountBackOnWhatIsOwed_AndLeavesThePaymentUntouched()
    {
        var sale = OnAccount();
        var payment = Pay(sale, 70m);
        var reverser = Guid.CreateVersion7();

        var reversal = sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "  Entered twice  ", reverser, Now.AddDays(3));

        Assert.Equal((SalePaymentKind.Reversal, 70m, payment.Id, "Entered twice", reverser), (reversal.Kind, reversal.Amount.Amount, reversal.ReversesPaymentId, reversal.ReversalReason, reversal.RecordedByUserId));
        Assert.Equal(-70m, reversal.SignedAmount);
        Assert.Equal((SalePaymentKind.Payment, 70m, Now.AddDays(1), "Cash"), (payment.Kind, payment.Amount.Amount, payment.PaidAtUtc, payment.MethodLabel));
        Assert.Equal(2, sale.Payments.Count);
        Assert.Equal(0m, sale.AmountPaid.Amount);
        Assert.Equal(100m, sale.Outstanding.Amount);
        Assert.Equal(2, sale.PaymentRevision);
    }

    [Fact]
    public void EveryPaymentRecord_OwnsItsAmount_EvenWhenBuiltFromAnotherRecordsAmount()
    {
        var shared = M(40m);
        var sale = Sale.Complete(Guid.CreateVersion7(), Organization, Gmd, Seller, Now, [Line()], Customer, Now.AddDays(30), shared, "Cash");
        var second = sale.RecordPayment(Guid.CreateVersion7(), shared, Now.AddDays(1), "Cash", Seller, Now.AddDays(1));

        var reversal = sale.ReversePayment(Guid.CreateVersion7(), second.Id, "Wrong", Seller, Now.AddDays(2));

        var instances = new[] { sale.Payments[0].Amount, second.Amount, reversal.Amount, shared };
        Assert.Equal(instances.Length, instances.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(instances, amount => Assert.Equal(40m, amount.Amount));
    }

    [Fact]
    public void AReversedPayment_CanBePaidAgain()
    {
        var sale = OnAccount();
        var payment = Pay(sale, 100m);
        sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Bounced", Seller, Now.AddDays(2));

        Pay(sale, 100m, daysAfter: 3);

        Assert.Equal(0m, sale.Outstanding.Amount);
        Assert.Equal(100m, sale.AmountPaid.Amount);
    }

    [Fact]
    public void APayment_CanBeReversedOnce_AndAReversalCannotBeReversed()
    {
        var sale = OnAccount();
        var payment = Pay(sale, 10m);
        var reversal = sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Wrong", Seller, Now.AddDays(2));

        Assert.Equal("sale.payment_already_reversed", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Again", Seller, Now.AddDays(3))));
        Assert.Equal("sale.payment_not_reversible", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), reversal.Id, "Undo", Seller, Now.AddDays(3))));
        Assert.Equal(2, sale.Payments.Count);
        Assert.Equal(0m, sale.AmountPaid.Amount);
    }

    [Fact]
    public void ThePaymentTakenWithTheSale_CanBeReversedToo()
    {
        var sale = OnAccount(paidAtSale: 100m);

        sale.ReversePayment(Guid.CreateVersion7(), sale.Id, "Customer returned the goods", Seller, Now.AddDays(1));

        Assert.Equal(100m, sale.Outstanding.Amount);
    }

    [Fact]
    public void AReversal_NeedsAPaymentOnTheSale_AReason_AndSensibleValues()
    {
        var sale = OnAccount();
        var payment = Pay(sale, 10m);

        Assert.Equal("sale.payment_not_found", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), Guid.CreateVersion7(), "Why", Seller, Now.AddDays(2))));
        Assert.Equal("sale.reversal_reason_required", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "  ", Seller, Now.AddDays(2))));
        Assert.Equal("sale.reversal_reason_required", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, null, Seller, Now.AddDays(2))));
        Assert.Equal("sale.reversal_reason_too_long", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, new string('x', 501), Seller, Now.AddDays(2))));
        Assert.Equal("sale.payment_id_required", CodeOf(() => sale.ReversePayment(Guid.Empty, payment.Id, "Why", Seller, Now.AddDays(2))));
        Assert.Equal("sale.payment_id_duplicate", CodeOf(() => sale.ReversePayment(payment.Id, payment.Id, "Why", Seller, Now.AddDays(2))));
        Assert.Equal("sale.payment_user_required", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Why", Guid.Empty, Now.AddDays(2))));
        Assert.Equal("sale.reversal_before_payment", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Why", Seller, Now)));
        Assert.Equal("sale.timestamp_not_utc", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), payment.Id, "Why", Seller, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.FromHours(1)))));
        Assert.Single(sale.Payments);
        Assert.Equal(1, sale.PaymentRevision);
    }

    [Fact]
    public void AWalkInSale_HasNoPaymentsToReverse()
    {
        var sale = WalkIn();

        Assert.Equal("sale.not_payable", CodeOf(() => sale.ReversePayment(Guid.CreateVersion7(), Guid.CreateVersion7(), "Why", Seller, Now.AddDays(1))));
    }

    // ---- voiding a sale with money on it

    [Fact]
    public void ASaleWithMoneyOnIt_CannotBeVoided_UntilThePaymentsAreReversed()
    {
        var sale = OnAccount(paidAtSale: 25m);

        Assert.Equal("sale.has_payments", CodeOf(() => sale.Void(Guid.CreateVersion7(), "Mistake", Now.AddMinutes(1))));
        Assert.Equal(SaleStatus.Completed, sale.Status);

        sale.ReversePayment(Guid.CreateVersion7(), sale.Id, "Refund", Seller, Now.AddMinutes(2));
        sale.Void(Guid.CreateVersion7(), "Mistake", Now.AddMinutes(3));

        Assert.Equal(SaleStatus.Voided, sale.Status);
        Assert.Equal(0m, sale.Outstanding.Amount);
    }

    [Fact]
    public void AnUnpaidSaleOnAccount_CanBeVoided_AndThenOwesNothing()
    {
        var sale = OnAccount();

        sale.Void(Guid.CreateVersion7(), "Customer cancelled", Now.AddMinutes(1));

        Assert.Equal(SaleStatus.Voided, sale.Status);
        Assert.Equal(0m, sale.Outstanding.Amount);
        Assert.Equal(Customer, sale.CustomerId);
    }

    // ---- deriving the balance from the records

    [Fact]
    public void TheAmountPaid_IsDerivedFromTheRecords_SoItCanBeReproduced()
    {
        var sale = OnAccount(paidAtSale: 10m);
        var second = Pay(sale, 20m);
        Pay(sale, 5.5m, daysAfter: 2);
        sale.ReversePayment(Guid.CreateVersion7(), second.Id, "Wrong customer", Seller, Now.AddDays(3));

        var fromRecords = sale.Payments.Sum(payment => payment.Kind == SalePaymentKind.Payment ? payment.Amount.Amount : -payment.Amount.Amount);

        Assert.Equal(15.5m, fromRecords);
        Assert.Equal(fromRecords, sale.AmountPaid.Amount);
        Assert.Equal(sale.Total.Amount - fromRecords, sale.Outstanding.Amount);
        Assert.Equal(84.5m, sale.Outstanding.Amount);
    }

    // ---- recognizing a repeated request

    [Fact]
    public void ARepeatedSettlement_IsRecognized_ByCustomerAndAmountPaidAtTheTime()
    {
        var partly = OnAccount(paidAtSale: 40m);
        var unpaid = OnAccount(paidAtSale: 0m);
        var full = OnAccount(paidAtSale: 100m);

        Assert.True(partly.HasSameSettlementAs(Customer, 40m));
        Assert.False(partly.HasSameSettlementAs(Customer, 41m));
        Assert.False(partly.HasSameSettlementAs(Customer, null)); // absent means paid in full
        Assert.True(unpaid.HasSameSettlementAs(Customer, 0m));
        Assert.False(unpaid.HasSameSettlementAs(Customer, null));
        Assert.True(full.HasSameSettlementAs(Customer, null));
        Assert.True(full.HasSameSettlementAs(Customer, 100m));
        Assert.False(full.HasSameSettlementAs(Guid.CreateVersion7(), null)); // another customer
        Assert.False(full.HasSameSettlementAs(null, null)); // no customer where there was one
    }

    [Fact]
    public void ARepeatedSettlement_IgnoresPaymentsRecordedSince()
    {
        var sale = OnAccount(paidAtSale: 40m);
        Pay(sale, 60m);

        Assert.True(sale.HasSameSettlementAs(Customer, 40m));
        var reversed = OnAccount(paidAtSale: 40m);
        reversed.ReversePayment(Guid.CreateVersion7(), reversed.Id, "Refund", Seller, Now.AddDays(1));
        Assert.True(reversed.HasSameSettlementAs(Customer, 40m));
    }

    [Fact]
    public void ForAWalkInSale_OnlyNoCustomerAndTheFullTotalMatch()
    {
        var sale = WalkIn();

        Assert.True(sale.HasSameSettlementAs(null, null));
        Assert.True(sale.HasSameSettlementAs(null, 100m));
        Assert.False(sale.HasSameSettlementAs(null, 50m));
        Assert.False(sale.HasSameSettlementAs(Customer, null));
    }
}
