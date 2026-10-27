using System;
using System.Collections.Generic;

namespace GameAnalytics.Client.Internal
{
    // Immutable per-event snapshot. Everything the wire needs is copied here at Track time under
    // the client lock, so once Track returns nothing the caller can still mutate is referenced by
    // the queued envelope, and batching/retries can only ever see this frozen value.
    internal sealed class EventEnvelope
    {
        public EventEnvelope(
            Guid eventId,
            string eventName,
            string category,
            string eventTimeUtc,
            string projectId,
            Guid? installationId,
            Guid sessionId,
            Guid? userId,
            Guid? transactionId,
            Guid? gameplayAttemptId,
            Guid? downloadId,
            EventContext context,
            string? dataRevision,
            IReadOnlyList<KeyValuePair<string, object?>>? properties)
        {
            EventId = eventId;
            EventName = eventName;
            Category = category;
            EventTimeUtc = eventTimeUtc;
            ProjectId = projectId;
            InstallationId = installationId;
            SessionId = sessionId;
            UserId = userId;
            TransactionId = transactionId;
            GameplayAttemptId = gameplayAttemptId;
            DownloadId = downloadId;
            Context = context;
            DataRevision = dataRevision;
            Properties = properties;
        }

        public Guid EventId { get; }

        public string EventName { get; }

        // The frozen category of the shared contract; it never changes in batching or retries.
        public string Category { get; }

        public string EventTimeUtc { get; }

        public string ProjectId { get; }

        public Guid? InstallationId { get; }

        public Guid SessionId { get; }

        public Guid? UserId { get; }

        public Guid? TransactionId { get; }

        public Guid? GameplayAttemptId { get; }

        public Guid? DownloadId { get; }

        public EventContext Context { get; }

        public string? DataRevision { get; }

        public IReadOnlyList<KeyValuePair<string, object?>>? Properties { get; }
    }
}
