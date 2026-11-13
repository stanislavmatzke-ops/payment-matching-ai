using Microsoft.EntityFrameworkCore;
using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Job.Tests;

/// <summary>A person undoes a match the AI made on its own (ADR 0012 amendment).</summary>
public class UndoAiMatchTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A sandbox whose AI payments the job decided with the stub model, and one AI auto match in it.</summary>
    private static async Task<(JobTestBed Bed, Guid Sandbox, AuditRecord AiMatch)> AiMatchedAsync(PostgresFixture postgres)
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var sandbox = await bed.SandboxAsync(seed: 11);
        await bed.Job(new CountingModel()).RunAsync(Ct);
        var aiMatch = (await bed.DecisionsAsync(sandbox)).FirstOrDefault(r => r.Layer == AuditRecord.AiLayer && r.Decision.Outcome == Outcome.AutoMatched);
        Assert.True(aiMatch is not null, "the starter data should give the stub model at least one automatic match");
        return (bed, sandbox, aiMatch);
    }

    [Fact]
    public async Task Undo_without_an_invoice_frees_the_ai_invoice_keeps_the_audit_and_counts_once()
    {
        var (bed, sandbox, aiMatch) = await AiMatchedAsync(postgres);
        var invoice = aiMatch.Decision.InvoiceNumbers[0];

        await using (var db = bed.For(sandbox))
        {
            var result = await new ReviewActions(db, bed.Time).UndoAiMatchAsync(aiMatch.PaymentId, [], [], Ct);
            Assert.Equal(ReviewOutcome.Done, result.Outcome);
        }

        await using (var db = bed.For(sandbox))
        {
            Assert.False(await db.InvoiceMatches.AnyAsync(m => m.InvoiceNumber == invoice, Ct));
            var state = await SandboxState.LoadAsync(db, null, Ct);
            Assert.Equal(PaymentState.Rejected, state.StateOf(aiMatch.PaymentId));
            Assert.Equal(ReviewAction.UndoneAi, state.Review(aiMatch.PaymentId)!.Action);
            Assert.Contains(state.OpenInvoices(), i => i.Number == invoice);

            // The AI's decision stays in the audit trail, unchanged.
            Assert.Equal(Outcome.AutoMatched, state.Decision(aiMatch.PaymentId)!.Decision.Outcome);
            Assert.False(state.CanUndoAi(aiMatch.PaymentId));
            Assert.Equal(ReviewOutcome.AlreadyDecided, (await new ReviewActions(db, bed.Time).UndoAiMatchAsync(aiMatch.PaymentId, [], [], Ct)).Outcome);
        }

        // The next run does not match the freed invoice again on its own: only a person does.
        await bed.Job(new CountingModel()).RunAsync(Ct);
        await using (var db = bed.For(sandbox))
        {
            Assert.False(await db.InvoiceMatches.AnyAsync(m => m.InvoiceNumber == invoice, Ct));
        }
    }

    [Fact]
    public async Task Undo_with_the_same_invoice_keeps_it_matched_and_rule_matches_cannot_be_undone()
    {
        var (bed, sandbox, aiMatch) = await AiMatchedAsync(postgres);

        await using (var db = bed.For(sandbox))
        {
            var state = await SandboxState.LoadAsync(db, null, Ct);
            var payment = state.Payment(aiMatch.PaymentId)!;
            var checks = state.Check(payment, aiMatch.Decision.InvoiceNumbers, freeing: payment.Id);
            Assert.DoesNotContain(checks, ProposalChecks.Blocking.Contains);
            var result = await new ReviewActions(db, bed.Time).UndoAiMatchAsync(aiMatch.PaymentId, aiMatch.Decision.InvoiceNumbers, checks, Ct);
            Assert.Equal(ReviewOutcome.Done, result.Outcome);
        }

        await using (var db = bed.For(sandbox))
        {
            Assert.Equal(
                aiMatch.Decision.InvoiceNumbers.Order(),
                await db.InvoiceMatches.Where(m => m.PaymentId == aiMatch.PaymentId).Select(m => m.InvoiceNumber).OrderBy(n => n).ToListAsync(Ct));
            var ruleMatch = (await bed.DecisionsAsync(sandbox)).First(r => r.Layer == AuditRecord.RuleLayer && r.Decision.Outcome == Outcome.AutoMatched);
            Assert.Equal(ReviewOutcome.NotActionable, (await new ReviewActions(db, bed.Time).UndoAiMatchAsync(ruleMatch.PaymentId, [], [], Ct)).Outcome);
        }
    }
}
