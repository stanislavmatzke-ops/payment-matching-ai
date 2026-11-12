using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentMatching.AiGateway;

namespace PaymentMatching.Data;

/// <summary>
/// The response cache of the hosted app (ADR 0013) as a provider decorator: an identical request (same
/// record/replay key, ADR 0007) gets the stored answer marked <see cref="LlmResponse.Cached"/>, without a
/// model call; a new request goes to <paramref name="inner"/> and its answer is stored. The answer is
/// still parsed and validated by code in every sandbox; the cache saves the call, not the checks.
/// </summary>
/// <param name="source">Which provider fills the cache ("live", "stub"); answers of one never serve the other.</param>
public sealed class CachingProvider(DbContextOptions<OperationsDbContext> options, string source, ILlmProvider inner, TimeProvider time)
    : ILlmProvider, IAnswerCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> ContainsAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var key = RecordReplayProvider.Key(request);
        await using var db = new OperationsDbContext(options);
        return await db.CachedAnswers.AnyAsync(c => c.Source == source && c.Key == key, cancellationToken);
    }

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var key = RecordReplayProvider.Key(request);
        var now = time.GetUtcNow();
        await using (var db = new OperationsDbContext(options))
        {
            var stored = await db.CachedAnswers.AsNoTracking()
                .Where(c => c.Source == source && c.Key == key)
                .Select(c => c.Response)
                .SingleOrDefaultAsync(cancellationToken);
            if (stored is not null)
            {
                await db.CachedAnswers.Where(c => c.Source == source && c.Key == key)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Hits, c => c.Hits + 1).SetProperty(c => c.LastHitAt, now), cancellationToken);
                var response = JsonSerializer.Deserialize<LlmResponse>(stored, Json) ?? throw new LlmException($"Cached answer {key} is empty.");
                return response with { Cached = true, Latency = TimeSpan.Zero };
            }
        }

        var fresh = await inner.CompleteJsonAsync(request, cancellationToken);
        await using (var db = new OperationsDbContext(options))
        {
            db.CachedAnswers.Add(new CachedAnswerRow
            {
                Source = source,
                Key = key,
                Model = fresh.Model,
                Response = JsonSerializer.Serialize(fresh with { Cached = false }, Json),
                CreatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another run stored the same request first; its answer is as good as ours.
            }
        }

        return fresh;
    }
}
