using System.Globalization;

namespace PaymentMatching.Data;

/// <summary>The three budget layers of ADR 0013 and the small-model-only switch (configuration <c>Ai:*</c>).</summary>
public sealed record AiBudgetOptions
{
    /// <summary>
    /// Model calls per sandbox and day (answers from the cache do not count). 20 lets the first run decide all
    /// AI payments of a new sandbox (~5–7, some escalated) at about $0.005; the app-wide layers guard the spend.
    /// </summary>
    public int CallsPerSandboxPerDay { get; init; } = 20;

    /// <summary>Model calls per day across all sandboxes.</summary>
    public int CallsPerDay { get; init; } = 50;

    /// <summary>Spend per calendar month (UTC) in USD.</summary>
    public decimal MonthlyUsd { get; init; } = 2m;

    /// <summary>
    /// What one call may cost at most, kept in reserve before calling: a small-model call costs ~$0.0002,
    /// an escalation to the large model ~$0.01, so $0.05 keeps the month under its budget with a margin.
    /// </summary>
    public decimal ReservePerCallUsd { get; init; } = 0.05m;

    /// <summary>Run on the small model only, never escalate (evals: same precision, about half the cost).</summary>
    public bool SmallModelOnly { get; init; }

    public static AiBudgetOptions Default { get; } = new();

    /// <summary>Reads <c>Ai:Budget:*</c> and <c>Ai:SmallModelOnly</c>; missing or invalid values keep the defaults.</summary>
    public static AiBudgetOptions Read(Func<string, string?> setting)
    {
        int Count(string key, int fallback) =>
            int.TryParse(setting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
        decimal Usd(string key, decimal fallback) =>
            decimal.TryParse(setting(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

        var d = Default;
        var reserve = Usd("Ai:Budget:ReservePerCallUsd", d.ReservePerCallUsd);
        return new AiBudgetOptions
        {
            CallsPerSandboxPerDay = Count("Ai:Budget:CallsPerSandboxPerDay", d.CallsPerSandboxPerDay),
            CallsPerDay = Count("Ai:Budget:CallsPerDay", d.CallsPerDay),
            MonthlyUsd = Usd("Ai:Budget:MonthlyUsd", d.MonthlyUsd),
            ReservePerCallUsd = reserve > 0 ? reserve : d.ReservePerCallUsd,
            SmallModelOnly = bool.TryParse(setting("Ai:SmallModelOnly"), out var small) && small,
        };
    }
}

/// <summary>Which budget layer runs out first.</summary>
public enum BudgetLimit
{
    None,

    /// <summary>The sandbox used its calls for today; its AI payments wait for the next run.</summary>
    Sandbox,

    /// <summary>The app used its calls for today; AI payments go to a person with a label.</summary>
    Day,

    /// <summary>The month's dollar budget is used up; AI payments go to a person with a label.</summary>
    Month,
}

/// <summary>How many model calls one sandbox may make now, and which layer runs out first.</summary>
public sealed record AiAllowance(int Calls, BudgetLimit LimitedBy)
{
    /// <summary>
    /// The three layers as one number: min(sandbox calls left today, app calls left today, calls the month's
    /// remaining dollars cover at <see cref="AiBudgetOptions.ReservePerCallUsd"/> each). On a tie the
    /// app-wide layer is named, so a used-up app budget is always labelled rather than silently deferred.
    /// </summary>
    public static AiAllowance For(AiBudgetOptions options, int sandboxCallsToday, UsageTotals appToday, UsageTotals appMonth)
    {
        var sandbox = Math.Max(0, options.CallsPerSandboxPerDay - sandboxCallsToday);
        var day = Math.Max(0, options.CallsPerDay - appToday.AiCalls);
        var month = (int)Math.Max(0m, Math.Floor((options.MonthlyUsd - appMonth.AiCostUsd) / options.ReservePerCallUsd));

        var calls = Math.Min(sandbox, Math.Min(day, month));
        var limit = month == calls ? BudgetLimit.Month
            : day == calls ? BudgetLimit.Day
            : BudgetLimit.Sandbox;
        return new AiAllowance(calls, limit);
    }

    /// <summary>The app-wide budget alone (dashboard): available, used up today, or used up this month.</summary>
    public static BudgetLimit AppState(AiBudgetOptions options, UsageTotals appToday, UsageTotals appMonth)
    {
        var allowance = For(options with { CallsPerSandboxPerDay = int.MaxValue }, 0, appToday, appMonth);
        return allowance.Calls > 0 ? BudgetLimit.None : allowance.LimitedBy;
    }
}
