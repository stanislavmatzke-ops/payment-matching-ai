using PaymentMatching.AiGateway;

namespace PaymentMatching.Matching;

/// <summary>
/// Turns the gateway result and the validation into the final decision. Automatic only when the
/// model says "match", validation passed, confidence is high enough and no flag needs a person.
/// </summary>
public sealed class AiDecisionMaker(MatchingOptions options)
{
    public Decision Decide(
        BankPayment payment,
        GatewayResult<AiMatchAnswer> result,
        ValidationResult? validation,
        bool payerIsKnownCustomer,
        bool injectionDetected)
    {
        var answer = result.Value;
        var decidedBy = result.Model is null ? DecidedBy.Policy : DecidedBy.Ai(result.Model);

        if (answer is null)
        {
            var why = result.BudgetExhausted ? "AI budget exhausted" : "no usable AI answer";
            IReadOnlyList<string> codeFlags = result.BudgetExhausted && result.Calls.Count == 0 ? [Flags.AiBudgetUsedUp] : [];
            return new Decision(payment.Id, Outcome.HumanReview, [], decidedBy, $"Rules could not decide and {why}.", Flagged(injectionDetected, codeFlags));
        }

        var flags = Flagged(injectionDetected, answer.Flags);
        if (flags.Contains(Flags.PromptInjection))
        {
            // Never show the attacker's target as a proposal.
            return new Decision(payment.Id, Outcome.NoMatch, [], decidedBy, "Bank data contains instructions for an AI; not matched.", flags, answer.Confidence);
        }

        if (answer.Decision == AiMatchAnswer.NoMatch)
        {
            return payerIsKnownCustomer
                ? new Decision(payment.Id, Outcome.HumanReview, [], decidedBy, "Paid by a known customer, but no invoice fits. " + answer.Reasoning, flags, answer.Confidence)
                : new Decision(payment.Id, Outcome.NoMatch, [], decidedBy, answer.Reasoning, flags, answer.Confidence);
        }

        var valid = validation?.IsValid == true;
        var auto = answer.Decision == AiMatchAnswer.Match
            && valid
            && answer.Confidence >= options.AutoMatchConfidence
            && !flags.Any(Flags.NeedHuman.Contains);

        if (auto)
        {
            return new Decision(payment.Id, Outcome.AutoMatched, answer.InvoiceNumbers, decidedBy, answer.Reasoning, flags, answer.Confidence);
        }

        var checks = validation is { IsValid: false } ? $" Failed checks: {string.Join(", ", validation.Failures)}." : "";
        return new Decision(payment.Id, Outcome.HumanReview, answer.InvoiceNumbers, decidedBy, answer.Reasoning + checks, flags, answer.Confidence);
    }

    private static IReadOnlyList<string> Flagged(bool injectionDetected, IReadOnlyList<string> modelFlags) =>
        injectionDetected && !modelFlags.Contains(Flags.PromptInjection)
            ? [.. modelFlags, Flags.PromptInjection]
            : modelFlags;
}
