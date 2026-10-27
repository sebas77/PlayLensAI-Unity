namespace GameAnalytics.Client
{
    // Immutable pull snapshot of the client's delivery state (AnalyticsClient.Stats). Counts
    // describe queued, confirmed and refused envelopes, never database rows or unique players.
    public sealed class DeliveryStats
    {
        internal DeliveryStats(long totalTracked, long totalDelivered, long totalRejected, int pending, DeliveryStatus lastStatus, int? lastHttpStatusCode)
        {
            TotalTracked = totalTracked;
            TotalDelivered = totalDelivered;
            TotalRejected = totalRejected;
            Pending = pending;
            LastStatus = lastStatus;
            LastHttpStatusCode = lastHttpStatusCode;
        }

        // Envelopes accepted by Track since construction.
        public long TotalTracked { get; }

        // Envelopes whose acknowledgement was confirmed. A lost or unconfirmed reply never counts.
        public long TotalDelivered { get; }

        // Envelopes a confirming acknowledgement refused. They left the queue without being
        // delivered, so TotalTracked == TotalDelivered + TotalRejected + Pending always holds.
        public long TotalRejected { get; }

        public int Pending { get; }

        // The outcome of the last delivery call; Pending before the first call ever completes,
        // because nothing has been confirmed yet.
        public DeliveryStatus LastStatus { get; }

        public int? LastHttpStatusCode { get; }
    }
}
