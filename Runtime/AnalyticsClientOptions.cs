using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameAnalytics.Client
{
    // Construction-time configuration of one AnalyticsClient. The three required values are
    // constructor arguments, so a missing base URI/project ID/installation ID is a compile-time
    // error rather than a silently defaulted option: in particular a null installation ID is an
    // explicit, intentional absence, never an omission that the client papers over by
    // generating a replacement. Everything else is optional and is validated and snapshotted
    // when the client is constructed.
    public sealed class AnalyticsClientOptions
    {
        public AnalyticsClientOptions(string baseUri, string projectId, Guid? installationId)
        {
            BaseUri = baseUri;
            ProjectId = projectId;
            InstallationId = installationId;
        }

        // Absolute base URI of the analytics API; the client joins its public routes onto it the
        // same way the JavaScript client does, ignoring trailing slashes.
        public string BaseUri { get; }

        public string ProjectId { get; }

        // The installation identity the host created and persists; the client never invents one.
        // Explicit null construction remains for the pending nullable-identity design, but the
        // current server requires a real installation before any identity can be confirmed.
        public Guid? InstallationId { get; }

        // Sender session identity; when omitted, the client generates exactly one Guid per
        // instance at construction.
        public Guid? SessionId { get; set; }

        // Client/device context applied to every event unless the Track call overrides a field.
        public EventContext? Context { get; set; }

        // Additional request headers forwarded unchanged on every request. A Content-Type entry
        // is ignored: the batch route's JSON media type is owned by the client.
        public IReadOnlyDictionary<string, string>? Headers { get; set; }

        // Retry budget per batch: the total number of attempts for each batch before it is reported as
        // failed. The floor of 1 means "no retry".
        public int MaxAttempts { get; set; } = 3;

        // Monotonic millisecond source used for event-clock anchoring. Defaults to Stopwatch;
        // the host wall clock is never consulted.
        public Func<double>? MonotonicMilliseconds { get; set; }

        // Cancellable delay used between delivery attempts. Defaults to Task.Delay.
        public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
    }
}
