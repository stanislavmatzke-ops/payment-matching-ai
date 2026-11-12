using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching.Tests;

/// <summary>Small hand-made datasets (fictional Demo Bank accounts) and the generated sample.</summary>
internal static class TestData
{
    public const string AlfaIban = "CZ0099900000000000000001";
    public const string BetaIban = "CZ0099900000000000000002";
    public const string StrangerIban = "CZ0099900000000000000099";

    public static readonly Customer Alfa = new("C001", "Alfa Testovaci s.r.o.", AlfaIban);
    public static readonly Customer Beta = new("C002", "Beta Testovaci a.s.", BetaIban);

    public static InvoiceRecord Invoice(string number, decimal amount, string customerId = "C001", string kind = "Regular") =>
        new(number, "2026" + number[5..].PadLeft(6, '0'), customerId, kind, new DateOnly(2026, 3, 1), amount, "CZK");

    public static BankPayment Payment(string id, decimal amount, string vs = "", string message = "", string iban = AlfaIban, string currency = "CZK") =>
        new(id, new DateOnly(2026, 3, 10), amount, currency, iban, "PLATCE", vs, message, $"{id};{amount};{currency};{iban};{vs};{message}");

    public static MatchingDataset Dataset(IReadOnlyList<InvoiceRecord> invoices, params BankPayment[] payments) =>
        new([Alfa, Beta], invoices, payments);

    public static MatchingDataset Sample { get; } = MatchingDataset.Load(RepoPaths.Combine("data", "sample"));

    public static Dictionary<string, Expected> ExpectedSample { get; } = File.ReadAllLines(RepoPaths.Combine("data", "sample", "expected.csv"))
        .Skip(1)
        .Where(l => l.Length > 0)
        .Select(MatchingDataset.SplitCsv)
        .ToDictionary(f => f[0], f => new Expected(f[1], f[2], f[3].Split('|', StringSplitOptions.RemoveEmptyEntries), f[5]));

    public static GatewayOptions GatewayOptions { get; } =
        new(new ModelRoute("claude-haiku-5-5", "low", 1024), new ModelRoute("claude-opus-5-5", "medium", 2048));

    public static AiGateway.AiGateway Gateway(ILlmProvider provider) => new(provider, GatewayOptions, PriceTable.Claude);

    internal sealed record Expected(string CaseType, string Outcome, string[] Invoices, string InjectionTarget);
}

/// <summary>Fake model driven by a function of the request.</summary>
internal sealed class FakeModel(Func<LlmRequest, string> answer) : ILlmProvider
{
    public List<LlmRequest> Requests { get; } = [];

    public static string Answer(string decision, IEnumerable<string> invoices, double confidence, params string[] flags) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            reasoning = "test",
            decision,
            invoice_numbers = invoices,
            confidence,
            flags,
        });

    public Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmResponse(request.Model, answer(request), "end_turn", new TokenUsage(1500, 120), TimeSpan.FromMilliseconds(700)));
    }
}
