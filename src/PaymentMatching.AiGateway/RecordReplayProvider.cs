using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentMatching.AiGateway;

public enum RecordingMode
{
    /// <summary>Only recorded answers; a missing recording is an error. Used by tests and CI.</summary>
    Replay,

    /// <summary>Recorded answers are reused, missing ones are fetched from the real provider and saved.</summary>
    Record,
}

/// <summary>
/// Record/replay decorator. The key is a SHA-256 of the whole request (model, effort, prompt,
/// schema, content), so a new prompt version or a changed input never replays a stale answer.
/// Recordings are small JSON files, readable in a pull request.
/// </summary>
public sealed class RecordReplayProvider(string directory, RecordingMode mode, ILlmProvider? live = null) : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var key = Key(request);
        var path = Path.Combine(directory, key + ".json");
        if (File.Exists(path))
        {
            var recording = JsonSerializer.Deserialize<Recording>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
                ?? throw new LlmException($"Recording {path} is empty.");
            return recording.Response;
        }

        if (mode == RecordingMode.Replay || live is null)
        {
            throw new RecordingMissingException(key, request.Model);
        }

        var response = await live.CompleteJsonAsync(request, cancellationToken);
        Directory.CreateDirectory(directory);
        var content = JsonSerializer.Serialize(new Recording(key, request.Model, request.UserContent, response), JsonOptions);
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n") + "\n", cancellationToken);
        return response;
    }

    public static string Key(LlmRequest request)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            request.Model, request.Effort, request.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.SystemPrompt, request.SchemaJson, request.UserContent,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    private sealed record Recording(
        string RequestHash,
        string Model,
        [property: JsonPropertyName("request")] string UserContent,
        LlmResponse Response);
}

public sealed class RecordingMissingException(string key, string model)
    : Exception($"No recorded answer for {model} request {key}. Run with --mode record (needs an API key).");
