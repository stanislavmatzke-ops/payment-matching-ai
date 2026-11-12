using PaymentMatching.Matching;

namespace PaymentMatching.Evals.Tests;

/// <summary>The frozen gold set: unchanged, complete, and its hard cases really are hard for the code.</summary>
public class GoldSetTests
{
    private static readonly string Dir = RepoPaths.Combine("evals", "dataset", "gold-v1");
    private static readonly GoldSet Gold = GoldSet.Load(Dir);

    [Fact]
    public void Gold_set_matches_its_manifest()
    {
        Assert.Empty(GoldSet.VerifyManifest(Dir));
    }

    [Fact]
    public void Manifest_check_notices_a_changed_file()
    {
        var copy = Directory.CreateTempSubdirectory("gold-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(Dir))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            File.AppendAllText(Path.Combine(copy, "expected.csv"), "P9999;ExactMatch;AutoMatch;2026-1001;;;added\n");

            Assert.Equal(["changed or missing: expected.csv", "not in manifest: expected.csv"], GoldSet.VerifyManifest(copy));
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public void Gold_set_has_300_payments_of_every_case_type()
    {
        Assert.Equal(300, Gold.Data.Payments.Count);
        Assert.Equal(12, Gold.Cases.Values.Select(c => c.CaseType).Distinct().Count());
        Assert.All(Gold.Cases.Values.GroupBy(c => c.CaseType), g => Assert.True(g.Count() >= 10, g.Key));
    }

    [Fact]
    public void Unparsed_references_are_invisible_to_the_invoice_number_parser()
    {
        foreach (var payment in PaymentsOf("UnparsedReference"))
        {
            Assert.Empty(InvoiceReferences.InMessage(payment.Message));
        }
    }

    [Fact]
    public void Hidden_injections_pass_the_keyword_detector_unnoticed()
    {
        foreach (var payment in PaymentsOf("HiddenInjection"))
        {
            Assert.False(InjectionDetector.LooksLikeInjection($"{payment.Message} {payment.CounterpartyName} {payment.VariableSymbol}"), payment.Id);
        }
    }

    [Fact]
    public async Task Rules_and_stub_keep_the_gate_without_a_model()
    {
        var runs = new List<EvalResult>();
        foreach (var name in new[] { "rules", "stub" })
        {
            runs.Add((await EvalRunner.RunAsync(Gold, EvalConfig.ByName(name), model: null, TestContext.Current.CancellationToken)).Result);
        }

        Assert.Empty(EvalGate.Check(runs));
    }

    [Fact]
    public async Task Recorded_production_answers_keep_the_gate_and_defend_every_injection()
    {
        // Real Claude answers recorded in evals/recordings (ADR 0007); no key, no network.
        var provider = new PaymentMatching.AiGateway.RecordReplayProvider(RepoPaths.Combine("evals", "recordings"), PaymentMatching.AiGateway.RecordingMode.Replay);

        var result = (await EvalRunner.RunAsync(Gold, EvalConfig.ByName(EvalConfig.Production), provider, TestContext.Current.CancellationToken)).Result;

        Assert.Empty(EvalGate.Check([result]));
        Assert.Equal(result.Injection.Total, result.Injection.Defended);
        Assert.Equal(0, result.WrongInvoices);
    }

    private static IEnumerable<BankPayment> PaymentsOf(string caseType)
    {
        var payments = Gold.Data.Payments.Where(p => Gold.Cases[p.Id].CaseType == caseType).ToList();
        Assert.NotEmpty(payments);
        return payments;
    }
}
