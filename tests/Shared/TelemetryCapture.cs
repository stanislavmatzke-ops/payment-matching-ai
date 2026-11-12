using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PaymentMatching.Tests.Shared;

/// <summary>
/// Collects the spans and metric measurements of one test without a collector or the OpenTelemetry SDK.
/// Tests run in parallel and listeners are process-wide, so everything is filtered by the trace of a root
/// span this capture starts: work done inside <c>using var capture = TelemetryCapture.Start();</c> belongs
/// to it (Activity.Current flows through async calls, and measurements are recorded on the caller's thread).
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private static readonly ActivitySource TestSource = new("PaymentMatching.Tests");

    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;
    private readonly Activity _root;
    private readonly Lock _lock = new();
    private readonly List<Activity> _spans = [];
    private readonly List<Measurement> _measurements = [];

    private TelemetryCapture(params string[] sources)
    {
        var names = new HashSet<string>(sources) { TestSource.Name };
        _activities = new ActivityListener
        {
            ShouldListenTo = s => names.Contains(s.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (_root is not null && a.TraceId == _root.TraceId && a != _root)
                {
                    lock (_lock)
                    {
                        _spans.Add(a);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (names.Contains(instrument.Meter.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => OnMeasurement(i, v, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => OnMeasurement(i, v, tags));
        _meters.Start();

        _root = TestSource.StartActivity("test", ActivityKind.Internal, parentContext: default)
            ?? throw new InvalidOperationException("Test span was not sampled.");
    }

    public ActivityTraceId TraceId => _root.TraceId;

    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_lock)
            {
                return [.. _spans];
            }
        }
    }

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_lock)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>Every tag value of every span and measurement, as text (for leak checks).</summary>
    public IEnumerable<(string Key, string Value)> AllTags =>
        Spans.SelectMany(s => s.TagObjects.Select(t => (t.Key, Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
                .Append(("span.name", s.DisplayName))
                .Append(("span.status", s.StatusDescription ?? ""))
                .Concat(s.Events.SelectMany(e => e.Tags.Select(t => (t.Key, Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")).Append(("event.name", e.Name)))))
            .Concat(Measurements.SelectMany(m => m.Tags.Select(t => (t.Key, Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""))));

    public static TelemetryCapture Start(params string[] sources) => new(sources.Length == 0 ? ["PaymentMatching"] : sources);

    public IReadOnlyList<Activity> Named(string name) => Spans.Where(s => s.DisplayName == name || s.OperationName == name).ToList();

    public double Sum(string instrument, Func<IReadOnlyDictionary<string, object?>, bool>? where = null) =>
        Measurements.Where(m => m.Instrument == instrument && (where is null || where(m.Tags))).Sum(m => m.Value);

    public void Dispose()
    {
        _root.Dispose();
        _activities.Dispose();
        _meters.Dispose();
    }

    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (_root is null || Activity.Current?.TraceId != _root.TraceId)
        {
            return;
        }

        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_lock)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }

    internal sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
}
