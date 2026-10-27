namespace GameAnalytics.Client.Internal
{
    // Explicit client lifetime states — no ad-hoc booleans. Track and SetActiveDataRevision are
    // accepted only while Open; Closing blocks them synchronously; Closed is terminal.
    internal enum LifecycleState
    {
        Uninitialized,
        Initializing,
        Open,
        Closing,
        Closed,
    }
}
