using System.Diagnostics;
using OpenTelemetry;

namespace PaymentMatching.Observability;

/// <summary>
/// Last line of defence before any exporter (ADR 0015): our own spans carry only codes and numbers by design,
/// but instrumentations we do not own may add a query string (<c>/dev-login?name=…</c>, sign-in callbacks),
/// a browser's user agent, a client address, or an error message with data in it. Those are removed here.
/// </summary>
internal sealed class RedactionProcessor : BaseProcessor<Activity>
{
    /// <summary>Tags that can carry personal data or text a person or a payer wrote.</summary>
    internal static readonly string[] Dropped =
    [
        "url.query", "user_agent.original", "client.address", "client.port", "network.peer.address",
        "network.peer.port", "enduser.id", "enduser.role", "enduser.scope", "http.request.body.size",
        "db.query.text", "db.statement", "exception.message", "exception.stacktrace",
    ];

    public override void OnEnd(Activity data)
    {
        foreach (var key in Dropped)
        {
            if (data.GetTagItem(key) is not null)
            {
                data.SetTag(key, null);
            }
        }

        if (data.GetTagItem("url.full") is string url && url.IndexOf('?', StringComparison.Ordinal) is var q and >= 0)
        {
            data.SetTag("url.full", url[..q]);
        }

        // Our spans set a category as the description; others may put an exception message there.
        if (data.Source.Name != AiGateway.Telemetry.Name && data.Status == ActivityStatusCode.Error && data.StatusDescription is not null)
        {
            data.SetStatus(ActivityStatusCode.Error);
        }
    }
}
