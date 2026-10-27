using System;

namespace GameAnalytics.Client
{
    // Engine-neutral permission signal for automatic flushing. The runtime adapter — a Unity player-loop
    // phase, a host frame boundary, a test double — owns the source and its lifetime; the SDK only
    // borrows it for the lifetime of one registration. A callback invocation means "delivering now is
    // safe", never "send immediately": the client still decides whether its periodic slot is due.
    public interface IFlushOpportunitySource
    {
        // Registers one callback and returns the subscription that detaches it. An implementation must
        // not invoke the callback during this call; every invocation after it returns is a permission
        // signal, and disposing the subscription is the only way the SDK releases the borrowed source.
        IDisposable Subscribe(Action onOpportunity);
    }
}
