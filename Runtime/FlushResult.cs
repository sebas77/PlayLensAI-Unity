namespace GameAnalytics.Client
{
    // Immutable result of one FlushAsync/CloseAsync call. Delivered counts only the envelopes
    // whose acknowledgement confirmed them in THIS call; Rejected counts the envelopes the same
    // acknowledgements refused. Both groups leave the queue. Pending is every envelope still
    // queued when the call finished, including work tracked while it ran. The counters describe
    // envelopes, not database rows or unique players.
    public sealed class FlushResult
    {
        internal FlushResult(int delivered, int rejected, int pending, DeliveryStatus status, int? lastHttpStatusCode)
        {
            Delivered = delivered;
            Rejected = rejected;
            Pending = pending;
            Status = status;
            LastHttpStatusCode = lastHttpStatusCode;
        }

        public int Delivered { get; }

        // Envelopes this call's acknowledgements refused; they are gone from the queue and are
        // never counted as delivered or retried.
        public int Rejected { get; }

        public int Pending { get; }

        public DeliveryStatus Status { get; }

        // The HTTP status observed on the last attempt, when the attempt reached the server.
        public int? LastHttpStatusCode { get; }
    }
}
