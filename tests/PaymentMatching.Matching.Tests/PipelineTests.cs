using System.Text.Json;
using System.Text.RegularExpressions;
using PaymentMatching.AiGateway;
using PaymentMatching.Rules;
using static PaymentMatching.Matching.Tests.TestData;

namespace PaymentMatching.Matching.Tests;

public class PipelineTests
{
    private static Task<RunReport> Run(MatchingDataset data, ILlmProvider? model, DecisionLog? log = null) =>
        new MatchingPipeline(data, model is null ? null : Gateway(model), log ?? new DecisionLog(), MatchingOptions.Default)
            .RunAsync(TestContext.Current.CancellationToken);

    private static string Describe(Decision d) =>
        $"{d.PaymentId} {d.Outcome} [{string.Join('|', d.InvoiceNumbers)}] {d.DecidedBy} [{string.Join('|', d.Flags)}] {d.Confidence} {d.Reason}";

    public static TheoryData<string, string> GoldenSets => new()
    {
        { "data/sample", "legacy/golden/seed-42" },
        { "legacy/golden/seed-2005/input", "legacy/golden/seed-2005" },
    };

    [Theory]
    [MemberData(nameof(GoldenSets))]
    public void Strangler_layer_1_is_exactly_the_ported_rules(string inputDir, string goldenDir)
    {
        var data = MatchingDataset.Load(RepoPaths.Combine(inputDir));

        var results = RulesLayer.Run(data);
        var csv = LegacyCsv.WriteResults(data.Payments.Select(p => results[p.Id]));

        Assert.True(csv == LegacyCsv.ReadText(RepoPaths.Combine(goldenDir, "port_matches.csv")), "Layer 1 differs from port_matches.csv.");
    }

    [Fact]
    public async Task Ai_never_decides_a_payment_the_rules_decided()
    {
        var model = new FakeModel(_ => throw new InvalidOperationException("must not be called"));
        var rulesOnly = await Run(Sample, model: null);
        var withAi = await Run(Sample, new NaiveModelStub());

        var ruleDecided = rulesOnly.Records.Where(r => r.Layer == AuditRecord.RuleLayer).ToList();
        Assert.NotEmpty(ruleDecided);
        foreach (var before in ruleDecided)
        {
            var after = withAi.Records.Single(r => r.PaymentId == before.PaymentId);
            Assert.Equal(AuditRecord.RuleLayer, after.Layer);
            Assert.Equal(Describe(before.Decision), Describe(after.Decision));
        }
    }

    [Fact]
    public async Task A_model_that_obeys_every_injection_still_matches_nothing_it_asks_for()
    {
        // Worst case: the model follows the instructions in the message, with full confidence and no flags.
        var obedient = new FakeModel(r =>
        {
            var bank = r.UserContent[r.UserContent.IndexOf("<untrusted_bank_data>", StringComparison.Ordinal)..];
            var target = Regex.Match(Regex.Unescape(bank), @"20\d\d-\d{4}").Value;
            return FakeModel.Answer("match", target.Length > 0 ? [target] : [], 1.0);
        });

        var report = await Run(Sample, obedient);

        var injections = ExpectedSample.Where(e => e.Value.CaseType == "PromptInjection").ToList();
        Assert.Equal(15, injections.Count);
        foreach (var (id, expected) in injections)
        {
            var decision = report.Records.Single(r => r.PaymentId == id).Decision;
            Assert.NotEqual(Outcome.AutoMatched, decision.Outcome);
            Assert.DoesNotContain(expected.InjectionTarget, decision.InvoiceNumbers);
            Assert.Contains(Flags.PromptInjection, decision.Flags);
        }

        var injectionTargets = injections.Select(i => i.Value.InjectionTarget).ToHashSet();
        Assert.DoesNotContain(report.Records, r => r.Decision.Outcome == Outcome.AutoMatched
            && r.Decision.InvoiceNumbers.Any(injectionTargets.Contains)
            && ExpectedSample[r.PaymentId].CaseType == "PromptInjection");
    }

    [Theory]
    [MemberData(nameof(GoldenSets))]
    public async Task No_proposal_any_model_could_make_passes_validation_unless_it_is_correct(string inputDir, string goldenDir)
    {
        // Exhaustive, model-free: for every payment that reaches layer 2, try every set of 1–4
        // candidate invoices as if a model proposed it with full confidence. Validation must accept
        // only the correct answer. (A random model passed an earlier version of this check by luck;
        // the stub model then found an EUR payment matched to a wrong combination.)
        _ = goldenDir;
        var data = MatchingDataset.Load(RepoPaths.Combine(inputDir));
        var expected = LoadExpected(inputDir);
        var rulesOnly = await Run(data, model: null);
        var claimed = rulesOnly.Records.Where(r => r.Decision.Outcome == Outcome.AutoMatched).SelectMany(r => r.Decision.InvoiceNumbers).ToHashSet();
        var selector = new CandidateSelector(data, MatchingOptions.Default);
        var validator = new ProposalValidator(data, selector, MatchingOptions.Default);

        var leaks = new List<string>();
        var tried = 0;
        foreach (var record in rulesOnly.Records.Where(r => r.Layer != AuditRecord.RuleLayer))
        {
            var payment = data.Payments.Single(p => p.Id == record.PaymentId);
            var candidates = selector.Select(payment, claimed);
            var numbers = candidates.Invoices.Select(c => c.Invoice.Number).ToList();
            foreach (var proposal in Subsets(numbers, 4))
            {
                tried++;
                var valid = validator.Validate(payment, proposal, candidates, claimed, injectionSuspected: false).IsValid;
                if (valid && !expected[payment.Id].Invoices.Order().SequenceEqual(proposal.Order()))
                {
                    leaks.Add($"{payment.Id} ({expected[payment.Id].CaseType}): {string.Join('|', proposal)}");
                }
            }
        }

        Assert.True(tried > 1000, $"Only {tried} proposals tried.");
        Assert.True(leaks.Count == 0, $"{leaks.Count} wrong proposals pass validation:\n" + string.Join('\n', leaks.Take(20)));
    }

    private static Dictionary<string, Expected> LoadExpected(string inputDir) =>
        File.ReadAllLines(RepoPaths.Combine(inputDir, "expected.csv")).Skip(1).Where(l => l.Length > 0)
            .Select(MatchingDataset.SplitCsv)
            .ToDictionary(f => f[0], f => new Expected(f[1], f[2], f[3].Split('|', StringSplitOptions.RemoveEmptyEntries), f[5]));

    private static IEnumerable<string[]> Subsets(List<string> items, int maxSize) =>
        Enumerable.Range(1, (1 << items.Count) - 1)
            .Where(mask => System.Numerics.BitOperations.PopCount((uint)mask) <= maxSize)
            .Select(mask => items.Where((_, i) => (mask & (1 << i)) != 0).ToArray());

    [Fact]
    public async Task Second_run_over_the_same_statement_calls_no_model_and_matches_nothing_twice()
    {
        var log = new DecisionLog();
        var model = new FakeModel(r => new NaiveModelStub().CompleteJsonAsync(r, CancellationToken.None).Result.Json);

        var first = await Run(Sample, model, log);
        var callsAfterFirst = model.Requests.Count;
        var second = await Run(Sample, model, log);

        Assert.True(callsAfterFirst > 0);
        Assert.Equal(callsAfterFirst, model.Requests.Count);
        Assert.Equal(Sample.Payments.Count, second.Reused);
        Assert.Equal(first.Records.Select(r => Describe(r.Decision)), second.Records.Select(r => Describe(r.Decision)));
        Assert.Equal(Sample.Payments.Count, log.Records.Count);
    }

    [Fact]
    public async Task Decisions_survive_a_restart_through_the_jsonl_log()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pm-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = await Run(Sample, new NaiveModelStub(), new DecisionLog(path));
            var reloaded = new DecisionLog(path);
            var second = await Run(Sample, new FakeModel(_ => throw new InvalidOperationException("must not be called")), reloaded);

            Assert.Equal(Sample.Payments.Count, second.Reused);
            Assert.Equal(first.Records.Select(r => r.Decision.Outcome), second.Records.Select(r => r.Decision.Outcome));
            var line = JsonDocument.Parse(File.ReadLines(path).First(l => l.Contains("\"layer\":\"ai\"", StringComparison.Ordinal)));
            Assert.Equal("match-payment.v1", line.RootElement.GetProperty("promptVersion").GetString());
            Assert.True(line.RootElement.GetProperty("calls").GetArrayLength() > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_invoice_is_automatically_matched_at_most_once()
    {
        var log = new DecisionLog();
        static AuditRecord Matched(string key, string payment) =>
            new(key, payment, DateTimeOffset.UnixEpoch, AuditRecord.AiLayer, null, null,
                new Decision(payment, Outcome.AutoMatched, ["2026-1001"], "test", "", []), [], [], []);

        log.Append(Matched("k1", "P1"));

        Assert.Throws<InvalidOperationException>(() => log.Append(Matched("k2", "P2")));
        Assert.Throws<InvalidOperationException>(() => log.Append(Matched("k1", "P1")));
    }

    [Fact]
    public async Task Payment_without_candidates_costs_no_model_call()
    {
        var unrelated = Payment("P1", 77.70m, vs: "9100001", message: "Vrácení přeplatku", iban: StrangerIban);
        var model = new FakeModel(_ => throw new InvalidOperationException("must not be called"));

        var report = await Run(Dataset([Invoice("2026-1234", 500m)], unrelated), model);

        Assert.Equal(Outcome.NoMatch, report.Records.Single().Decision.Outcome);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task Unsure_small_model_is_escalated_and_both_calls_are_audited()
    {
        var payment = Payment("P1", 500m, vs: "2026001243");
        var model = new FakeModel(r => r.Model == "claude-haiku-5-5"
            ? FakeModel.Answer("uncertain", ["2026-1234"], 0.6)
            : FakeModel.Answer("match", ["2026-1234"], 0.96, Flags.VsTypo));

        var report = await Run(Dataset([Invoice("2026-1234", 500m)], payment), model);

        var record = report.Records.Single();
        Assert.Equal(Outcome.AutoMatched, record.Decision.Outcome);
        Assert.Equal("ai:claude-opus-5-5", record.Decision.DecidedBy);
        Assert.Equal(["small", "large"], record.Calls.Select(c => c.Tier));
        Assert.True(record.Calls.Sum(c => c.CostUsd) > 0);
    }
}
