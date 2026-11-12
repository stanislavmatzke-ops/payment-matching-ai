using PaymentMatching.Data;
using PaymentMatching.Matching;

namespace PaymentMatching.Job.Tests;

public class DailyBatchTests
{
    private static readonly DateOnly Start = new(2026, 11, 12);

    private static IEnumerable<DateOnly> Days(int count) => Enumerable.Range(0, count).Select(Start.AddDays);

    /// <summary>Payments of the batch that the rules leave for the AI layer.</summary>
    private static async Task<int> ForAiAsync(MatchingDataset data)
    {
        var store = new DecisionLog();
        await new MatchingPipeline(data, null, store, MatchingOptions.Default with { DeferAiWithoutGateway = true })
            .RunAsync(TestContext.Current.CancellationToken);
        return data.Payments.Count - store.Records.Count;
    }

    [Fact]
    public void Same_day_same_batch_another_day_another_batch()
    {
        var a = DailyBatch.For(Start);
        var b = DailyBatch.For(Start);
        var next = DailyBatch.For(Start.AddDays(1));

        Assert.Equal(a.Data.Payments.Select(p => p.RawLine), b.Data.Payments.Select(p => p.RawLine));
        Assert.Equal(a.Data.Invoices, b.Data.Invoices);
        Assert.Empty(a.Data.Invoices.Select(i => i.Number).Intersect(next.Data.Invoices.Select(i => i.Number)));
        Assert.Empty(a.Data.Payments.Select(p => p.Id).Intersect(next.Data.Payments.Select(p => p.Id)));
    }

    [Fact]
    public async Task Every_day_of_a_year_has_3_to_5_payments_and_1_or_2_for_the_model()
    {
        foreach (var day in Days(400))
        {
            var batch = DailyBatch.For(day);
            Assert.InRange(batch.Data.Payments.Count, 3, 5);
            var ai = await ForAiAsync(batch.Data);
            Assert.True(ai is 1 or 2, $"{day:yyyy-MM-dd}: {ai} payments need the model.");
        }
    }

    [Fact]
    public void Ids_carry_the_date_and_never_collide_with_starter_data()
    {
        var batch = DailyBatch.For(Start);
        var starter = new SandboxProvisioner(PaymentsDb.Options("Host=unused"), new SandboxOptions(), TimeProvider.System).StarterData(7, Start);

        Assert.All(batch.Data.Payments, p => Assert.StartsWith("P261112-", p.Id, StringComparison.Ordinal));
        Assert.All(batch.Data.Customers, c => Assert.StartsWith("C261112-", c.Id, StringComparison.Ordinal));
        Assert.Empty(batch.Data.Invoices.Select(i => i.Number).Intersect(starter.Invoices.Select(i => i.Number)));
        Assert.Empty(batch.Data.Invoices.Select(i => i.VariableSymbol).Intersect(starter.Invoices.Select(i => i.VariableSymbol)));
        Assert.Empty(batch.Data.Payments.Select(p => p.Id).Intersect(starter.Payments.Select(p => p.Id)));
        Assert.All(batch.Data.Payments, p => Assert.Matches("^[A-Za-z0-9-]{1,40}$", p.Id));
    }
}
