using Microsoft.EntityFrameworkCore;
using PTGOilSystem.Web.Data;
using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services;
using PTGOilSystem.Web.Services.Accounting;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// In-memory coverage for the central payment correction flow and the revision/reversal keys it
/// relies on. The double-entry posting itself is asserted against real PostgreSQL in
/// PaymentAccountingAdapterTests; here the adapter is faked so the orchestration — which
/// allocations get reversed, and whether the journal is re-synced — can be verified without a
/// database engine.
/// </summary>
public sealed class PaymentCorrectionServiceTests
{
    // ---- source-event / journal-number keys ----

    [Fact]
    public void CreatedSourceEvent_Revision0_Keeps_Legacy_Key()
    {
        Assert.Equal("Payment:7:Created", PaymentAccountingAdapter.BuildCreatedSourceEventId(7));
        Assert.Equal("Payment:7:Created", PaymentAccountingAdapter.BuildCreatedSourceEventId(7, 0));
    }

    [Fact]
    public void CreatedSourceEvent_LaterRevisions_Are_Suffixed()
        => Assert.Equal("Payment:7:Created:2", PaymentAccountingAdapter.BuildCreatedSourceEventId(7, 2));

    [Fact]
    public void ReversedSourceEvent_Is_Revision_Scoped()
        => Assert.Equal("Payment:7:Reversed:1", PaymentAccountingAdapter.BuildReversedSourceEventId(7, 1));

    [Fact]
    public void PaymentRevisionNumber_Revision0_Is_Legacy_And_Reversal_Is_Distinct()
    {
        var gen = new AccountingJournalNumberGenerator();
        Assert.Equal(gen.ForPayment(1, 5), gen.ForPaymentRevision(1, 5, 0));
        Assert.Equal("PAY-000001-0000000005-R001", gen.ForPaymentRevision(1, 5, 1));
        Assert.Equal("PAYR-000001-0000000005-R000", gen.ForPaymentReversal(1, 5, 0));
    }

    // ---- ApplyAsync orchestration ----

    [Fact]
    public async Task ReverseAllocations_Reverses_Only_Active_And_Counts_Them()
    {
        await using var db = NewDb();
        var payment = SeedPayment(db, id: 10);
        SeedAllocation(db, id: 1, paymentId: 10, SupplierPaymentAllocationStatus.Active);
        SeedAllocation(db, id: 2, paymentId: 10, SupplierPaymentAllocationStatus.Active);
        SeedAllocation(db, id: 3, paymentId: 10, SupplierPaymentAllocationStatus.Reversed);
        SeedAllocation(db, id: 4, paymentId: 99, SupplierPaymentAllocationStatus.Active); // other payment
        await db.SaveChangesAsync();

        var allocations = new RecordingAllocationService();
        var service = new PaymentCorrectionService(db, allocations, paymentAccounting: null);

        var result = await service.ApplyAsync(payment, reverseAllocations: true, resyncJournal: false,
            reason: "fix", userName: "tester");

        Assert.Equal(2, result.ReversedAllocationCount);
        Assert.Equal(new[] { 1, 2 }, allocations.ReversedIds.OrderBy(x => x).ToArray());
        Assert.All(allocations.Reasons, r => Assert.Equal("fix", r));
    }

    [Fact]
    public async Task ReverseAllocations_False_Reverses_Nothing()
    {
        await using var db = NewDb();
        var payment = SeedPayment(db, id: 10);
        SeedAllocation(db, id: 1, paymentId: 10, SupplierPaymentAllocationStatus.Active);
        await db.SaveChangesAsync();

        var allocations = new RecordingAllocationService();
        var service = new PaymentCorrectionService(db, allocations, paymentAccounting: null);

        var result = await service.ApplyAsync(payment, reverseAllocations: false, resyncJournal: false,
            reason: "fix", userName: "tester");

        Assert.Equal(0, result.ReversedAllocationCount);
        Assert.Empty(allocations.ReversedIds);
    }

    [Fact]
    public async Task ResyncJournal_Reverses_Then_Reposts_In_Order()
    {
        await using var db = NewDb();
        var payment = SeedPayment(db, id: 10);
        await db.SaveChangesAsync();

        var adapter = new RecordingPaymentAdapter();
        var service = new PaymentCorrectionService(db, new RecordingAllocationService(), adapter);

        var result = await service.ApplyAsync(payment, reverseAllocations: false, resyncJournal: true,
            reason: "fix", userName: "tester");

        Assert.True(result.JournalResynced);
        Assert.Equal(new[] { "Reverse", "Post" }, adapter.Calls.ToArray());
    }

    [Fact]
    public async Task ResyncJournal_False_Does_Not_Touch_Adapter()
    {
        await using var db = NewDb();
        var payment = SeedPayment(db, id: 10);
        await db.SaveChangesAsync();

        var adapter = new RecordingPaymentAdapter();
        var service = new PaymentCorrectionService(db, new RecordingAllocationService(), adapter);

        var result = await service.ApplyAsync(payment, reverseAllocations: false, resyncJournal: false,
            reason: "fix", userName: "tester");

        Assert.False(result.JournalResynced);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task ResyncJournal_With_No_Adapter_Is_A_Safe_Noop()
    {
        await using var db = NewDb();
        var payment = SeedPayment(db, id: 10);
        await db.SaveChangesAsync();

        var service = new PaymentCorrectionService(db, new RecordingAllocationService(), paymentAccounting: null);

        var result = await service.ApplyAsync(payment, reverseAllocations: false, resyncJournal: true,
            reason: "fix", userName: "tester");

        Assert.False(result.JournalResynced);
    }

    // ---- helpers ----

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PaymentTransaction SeedPayment(ApplicationDbContext db, int id)
    {
        var payment = new PaymentTransaction
        {
            Id = id,
            PaymentDate = new DateTime(2026, 2, 1),
            Direction = PaymentDirection.Out,
            PaymentKind = PaymentKind.SupplierPayment,
            CashAccountId = 1,
            SupplierId = 1,
            Amount = 100m,
            Currency = "USD",
            AppliedFxRateToUsd = 1m,
            AmountUsd = 100m
        };
        db.PaymentTransactions.Add(payment);
        return payment;
    }

    private static void SeedAllocation(
        ApplicationDbContext db, int id, int paymentId, SupplierPaymentAllocationStatus status)
        => db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            Id = id,
            PaymentTransactionId = paymentId,
            ContractId = 1,
            AllocationDate = new DateTime(2026, 2, 1),
            Status = status
        });

    private sealed class RecordingAllocationService : ISupplierPaymentAllocationService
    {
        public List<int> ReversedIds { get; } = new();
        public List<string> Reasons { get; } = new();

        public Task<decimal> GetAllocatableBalanceUsdAsync(int paymentTransactionId, CancellationToken ct = default)
            => Task.FromResult(0m);

        public Task<decimal> GetAllocatablePaymentAmountAsync(int paymentTransactionId, CancellationToken ct = default)
            => Task.FromResult(0m);

        public Task<SupplierPaymentAllocation> CreateAsync(
            SupplierPaymentAllocationCreateRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SupplierPaymentAllocation> ReverseAsync(
            SupplierPaymentAllocationReverseRequest request, CancellationToken ct = default)
        {
            ReversedIds.Add(request.AllocationId);
            Reasons.Add(request.ReversalReason);
            return Task.FromResult(new SupplierPaymentAllocation
            {
                Id = request.AllocationId,
                Status = SupplierPaymentAllocationStatus.Reversed
            });
        }
    }

    private sealed class RecordingPaymentAdapter : IPaymentAccountingAdapter
    {
        public List<string> Calls { get; } = new();

        public Task<PaymentAccountingResult> TryPostPaymentAsync(
            PaymentTransaction payment, CancellationToken cancellationToken = default)
        {
            Calls.Add("Post");
            return Task.FromResult(new PaymentAccountingResult(PaymentPostingStatus.Posted, null, null));
        }

        public Task<PaymentAccountingResult> TryPostPaymentReversalAsync(
            PaymentTransaction payment, CancellationToken cancellationToken = default)
        {
            Calls.Add("Reverse");
            return Task.FromResult(new PaymentAccountingResult(PaymentPostingStatus.Posted, null, null));
        }
    }
}
