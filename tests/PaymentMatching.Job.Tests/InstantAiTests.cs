using PaymentMatching.Data;
using PaymentMatching.Matching;
using PaymentMatching.Testing;

namespace PaymentMatching.Job.Tests;

/// <summary>ADR 0017: a new sandbox gets its AI decisions at sign-in from the cache the job filled, without a model call.</summary>
public class InstantAiTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SandboxProvisioner Provisioner(JobTestBed bed, string source = "live") =>
        new(bed.Payments, new SandboxOptions(), bed.Time, new InstantAiOptions(bed.Operations, source, SmallModelOnly: false));

    private static async Task<Guid> SignInAsync(JobTestBed bed, string subject, string source = "live")
    {
        var result = await Provisioner(bed, source).EnsureAsync(new SignInIdentity("dev", subject, subject, $"{subject}@example.test"), Ct);
        Assert.Equal(ProvisionStatus.Created, result.Status);
        return result.TenantId!.Value;
    }

    [Fact]
    public async Task After_the_job_a_sign_in_gets_the_ai_decisions_from_the_cache_without_calling_a_model()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        var model = new CountingModel();

        var report = await bed.Job(model, starter: true).RunAsync(Ct);
        var precomputed = model.Requests.Count;
        Assert.True(precomputed > 0);
        Assert.Equal(precomputed, report.Starter!.ModelCalls);
        Assert.Equal(0, report.Starter.WaitingForAi);

        var alice = await SignInAsync(bed, "alice");
        var bob = await SignInAsync(bed, "bob");

        Assert.Equal(precomputed, model.Requests.Count);
        foreach (var sandbox in new[] { alice, bob })
        {
            var states = await bed.StatesAsync(sandbox);
            Assert.DoesNotContain(PaymentState.WaitingForAi, states.Values);
            var ai = (await bed.DecisionsAsync(sandbox)).Where(r => r.Layer == AuditRecord.AiLayer).ToList();
            Assert.NotEmpty(ai);
            Assert.All(ai, r => Assert.All(r.Calls, c => Assert.True(c.Cached)));
        }

        // The same answers as the precomputation, decided separately in each sandbox.
        var aliceAi = (await bed.DecisionsAsync(alice)).Where(r => r.Layer == AuditRecord.AiLayer).ToDictionary(r => r.PaymentId, r => r.Decision.InvoiceNumbers);
        var bobAi = (await bed.DecisionsAsync(bob)).Where(r => r.Layer == AuditRecord.AiLayer).ToDictionary(r => r.PaymentId, r => r.Decision.InvoiceNumbers);
        Assert.Equal(aliceAi.Keys.Order(), bobAi.Keys.Order());

        // Cached answers cost nothing and do not count against the sandboxes' own daily limit.
        var next = await bed.Job(model, starter: true).RunAsync(Ct);
        Assert.Equal(precomputed, model.Requests.Count);
        Assert.Equal(0, next.ModelCalls);
    }

    [Fact]
    public async Task Without_a_cached_answer_ai_payments_wait_for_the_job_and_no_model_is_called()
    {
        var bed = await JobTestBed.CreateAsync(postgres);

        var sandbox = await SignInAsync(bed, "carol");

        Assert.Contains(PaymentState.WaitingForAi, (await bed.StatesAsync(sandbox)).Values);
        Assert.DoesNotContain(await bed.DecisionsAsync(sandbox), r => r.Layer == AuditRecord.AiLayer);
    }

    [Fact]
    public async Task A_stub_answer_never_serves_a_live_sign_in()
    {
        var bed = await JobTestBed.CreateAsync(postgres);
        await bed.Job(new CountingModel(), source: "stub", starter: true).RunAsync(Ct);

        var live = await SignInAsync(bed, "dave", source: "live");
        var stub = await SignInAsync(bed, "erin", source: "stub");

        Assert.Contains(PaymentState.WaitingForAi, (await bed.StatesAsync(live)).Values);
        Assert.DoesNotContain(PaymentState.WaitingForAi, (await bed.StatesAsync(stub)).Values);
    }

    [Fact]
    public void Starter_set_changes_on_monday_and_is_the_same_all_week()
    {
        var monday = new DateOnly(2026, 11, 9);

        Assert.All(Enumerable.Range(0, 7), d => Assert.Equal(monday, StarterSet.WeekOf(monday.AddDays(d))));
        Assert.Equal(monday.AddDays(7), StarterSet.WeekOf(monday.AddDays(7)));
        Assert.Equal(20261109, StarterSet.SeedOf(monday));
    }
}
