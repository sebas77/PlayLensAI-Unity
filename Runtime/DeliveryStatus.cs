namespace GameAnalytics.Client
{
    // Outcome of one FlushAsync/CloseAsync delivery call. Complete and Pending describe a
    // successful call (Complete means the queue is empty afterwards, Pending means work is
    // still queued — either newly tracked or not yet drained); the three failure members
    // describe why a delivery attempt stopped. Ordinary delivery failures are results,
    // never thrown callbacks.
    public enum DeliveryStatus
    {
        Complete,
        Pending,
        NetworkFailure,
        HttpFailure,
        InvalidResponse,
    }
}
