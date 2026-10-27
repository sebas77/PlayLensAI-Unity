namespace GameAnalytics.Client
{
    // Immutable result of one FlushAsync/CloseAsync call. Delivered counts only the envelopes
    // whose acknowledgement was confirmed in THIS call; Pending is every envelope still queued
    // when the call finished, including work tracked while it ran. The counters describe
    // envelopes, not database rows or unique players.
    public sealed class FlushResult
    {
        internal FlushResult(int delivered, int pending, DeliveryStatus status, int? lastHttpStatusCode)
        {
            Delivered = delivered;
            Pending = pending;
            Status = status;
            LastHttpStatusCode = lastHttpStatusCode;
        }

        public int Delivered { get; }

        public int Pending { get; }

        public DeliveryStatus Status { get; }

        // The HTTP status observed on the last attempt, when the attempt reached the server.
        public int? LastHttpStatusCode { get; }
    }
}
