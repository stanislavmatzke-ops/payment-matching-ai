using Microsoft.EntityFrameworkCore;

namespace PaymentMatching.Data;

/// <summary>What to add to a day's counters.</summary>
public sealed record UsageDelta(int AiCalls = 0, decimal AiCostUsd = 0m, int CacheHits = 0, int SandboxesRefilled = 0, int SandboxesDeleted = 0, int SignInsTurnedAway = 0);

/// <summary>Totals over a period.</summary>
public sealed record UsageTotals(int AiCalls, decimal AiCostUsd, int CacheHits, int SandboxesRefilled, int SandboxesDeleted, int SignInsTurnedAway)
{
    public static UsageTotals Zero { get; } = new(0, 0m, 0, 0, 0, 0);

    /// <summary>Of the answers the job needed, the share served from the cache.</summary>
    public double? CacheHitRate => AiCalls + CacheHits == 0 ? null : (double)CacheHits / (AiCalls + CacheHits);
}

/// <summary>
/// The app-wide daily usage ledger behind the AI budgets and the admin overview. Additions are atomic
/// updates in the database (<c>x = x + n</c>), so concurrent writers do not lose counts.
/// </summary>
public sealed class UsageLedger(DbContextOptions<OperationsDbContext> options)
{
    public async Task AddAsync(DateOnly day, UsageDelta delta, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = new OperationsDbContext(options);
            var updated = await db.DailyUsage.Where(d => d.Day == day).ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.AiCalls, d => d.AiCalls + delta.AiCalls)
                    .SetProperty(d => d.AiCostUsd, d => d.AiCostUsd + delta.AiCostUsd)
                    .SetProperty(d => d.CacheHits, d => d.CacheHits + delta.CacheHits)
                    .SetProperty(d => d.SandboxesRefilled, d => d.SandboxesRefilled + delta.SandboxesRefilled)
                    .SetProperty(d => d.SandboxesDeleted, d => d.SandboxesDeleted + delta.SandboxesDeleted)
                    .SetProperty(d => d.SignInsTurnedAway, d => d.SignInsTurnedAway + delta.SignInsTurnedAway),
                cancellationToken);
            if (updated == 1)
            {
                return;
            }

            db.DailyUsage.Add(new DailyUsageRow
            {
                Day = day,
                AiCalls = delta.AiCalls,
                AiCostUsd = delta.AiCostUsd,
                CacheHits = delta.CacheHits,
                SandboxesRefilled = delta.SandboxesRefilled,
                SandboxesDeleted = delta.SandboxesDeleted,
                SignInsTurnedAway = delta.SignInsTurnedAway,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                // Another writer created the day's row first: add to it on the second attempt.
            }
        }

        throw new InvalidOperationException($"Could not update the usage of {day:yyyy-MM-dd}.");
    }

    public async Task<UsageTotals> TotalsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = new OperationsDbContext(options);
        var rows = await db.DailyUsage.AsNoTracking().Where(d => d.Day >= from && d.Day <= to).ToListAsync(cancellationToken);
        return new UsageTotals(
            rows.Sum(r => r.AiCalls),
            rows.Sum(r => r.AiCostUsd),
            rows.Sum(r => r.CacheHits),
            rows.Sum(r => r.SandboxesRefilled),
            rows.Sum(r => r.SandboxesDeleted),
            rows.Sum(r => r.SignInsTurnedAway));
    }

    public Task<UsageTotals> DayAsync(DateOnly day, CancellationToken cancellationToken = default) => TotalsAsync(day, day, cancellationToken);

    public Task<UsageTotals> MonthAsync(DateOnly day, CancellationToken cancellationToken = default) =>
        TotalsAsync(new DateOnly(day.Year, day.Month, 1), day, cancellationToken);
}
