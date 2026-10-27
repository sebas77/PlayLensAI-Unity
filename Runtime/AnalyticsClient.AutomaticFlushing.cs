using System;
using System.Threading;
using System.Threading.Tasks;
using GameAnalytics.Client.Internal;

namespace GameAnalytics.Client
{
    // Automatic flushing policy of the engine-neutral client: the runtime grants permission through a
    // borrowed IFlushOpportunitySource, an accepted Funnel event asks for delivery immediately, and both
    // triggers drain the ENTIRE captured queue through the existing FlushAsync delivery. Nothing here is a
    // second sender, a timer, a new transport or an event bus — the registration reuses the client's
    // queue, capture, batching, acknowledgement matching, retry policy and close sequencing, and it only
    // holds the same two locks the rest of the client already has.
    public sealed partial class AnalyticsClient
    {
        // Enables the one automatic registration of this client. Requires an open client, a borrowed
        // source and both host callbacks; a second active registration is rejected rather than taking over
        // the first one's ownership. The returned registration owns only its source subscription, its
        // automatic drain and its cancellation: disposing it detaches and cancels WITHOUT flushing, which
        // makes automatic flushing a consent opt-out seam instead of a rewrite of retained identities.
        public IDisposable StartAutomaticFlushing(IFlushOpportunitySource source, Action<FlushResult> onResult, Action<Exception> onError)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (onResult == null)
                throw new ArgumentNullException(nameof(onResult));
            if (onError == null)
                throw new ArgumentNullException(nameof(onError));

            AutomaticFlushingController controller;
            bool deliverRetained;
            lock (_gate)
            {
                if (_state != LifecycleState.Open)
                    throw new InvalidOperationException("StartAutomaticFlushing: the client is not open.");
                if (_automaticFlushing != null)
                    throw new InvalidOperationException("StartAutomaticFlushing: an automatic flushing registration is already active.");

                // The scheduling anchor is sampled before anything is owned, so an unavailable clock is the
                // caller's failure instead of a half-enabled registration. It is the same snapshotted
                // monotonic option the event clock reads; this policy never introduces a second clock.
                var anchorMilliseconds = _eventClock.ReadMonotonicMilliseconds();

                // Enable scans the retained queue once: Funnel work tracked before the registration exists
                // is an immediate request, so opting in never silently forgets it. The request is consumed
                // by the capture that delivers it, exactly like one recorded by a later Track call.
                deliverRetained = HasRetainedFunnel();
                _automaticRequestPending = _automaticRequestPending || deliverRetained;
                controller = new AutomaticFlushingController(this, onResult, onError, anchorMilliseconds);
                _automaticFlushing = controller;
            }

            try
            {
                controller.Attach(source.Subscribe(controller.Opportunity));
            }
            catch
            {
                // A source that refuses the subscription leaves no registration behind: the failure is the
                // caller's and the client goes back to manual-only delivery.
                lock (_gate)
                {
                    if (ReferenceEquals(_automaticFlushing, controller))
                        _automaticFlushing = null;
                }

                controller.Dispose();
                throw;
            }

            if (deliverRetained)
                controller.RequestDrain();

            return controller;
        }

        // Close stops automatic work BEFORE its existing final serialized drain: the registration is
        // detached and cancelled silently, so no automatic delivery or host callback competes with the
        // close for the gate. Never called under _gate.
        void StopAutomaticFlushing()
        {
            AutomaticFlushingController? controller;
            lock (_gate)
            {
                controller = _automaticFlushing;
                _automaticFlushing = null;
            }

            controller?.Dispose();
        }

        // The registration releases only itself, so a registration that replaced it after a suspension is
        // left alone.
        void ReleaseAutomaticFlushing(AutomaticFlushingController controller)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_automaticFlushing, controller))
                    _automaticFlushing = null;
            }
        }

        // The scheduling clock is the same snapshotted monotonic option the event clock reads. An
        // unavailable or non-finite reading stays caller-visible, which is exactly what lets the
        // registration suspend itself and report instead of inventing a time.
        double ReadSchedulingMilliseconds() => _eventClock.ReadMonotonicMilliseconds();

        // Records that an automatic delivery is authorized — by an accepted Funnel track or by a due
        // runtime permission — and not yet consumed. It takes _gate itself, so it is safe both from Track
        // (which already holds it: the lock is reentrant) and from the registration.
        void AuthorizeAutomaticDelivery()
        {
            lock (_gate)
                _automaticRequestPending = true;
        }

        // True while the client holds an automatic authorization that no capture has consumed yet. It takes
        // _gate itself, so a caller that already holds it simply re-enters.
        bool HasAutomaticRequest()
        {
            lock (_gate)
                return _automaticRequestPending;
        }

        // True when the retained queue already holds a Funnel envelope; the enable scan uses it so work
        // tracked before the registration is not forgotten. It takes _gate itself, so the scan that calls it
        // under the same lock simply re-enters.
        bool HasRetainedFunnel()
        {
            lock (_gate)
            {
                foreach (var envelope in _queue)
                {
                    if (string.Equals(envelope.Category, FunnelCategory, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }
        }

        // One automatic delivery through the existing serialized gate. Null means the opportunity was spent,
        // so it touches neither the network nor the Stats snapshot. The authorization is consumed by the
        // capture inside FlushCoreAsync, under the same lock that takes the envelopes, so this can never
        // repeat work another capture already delivered.
        async Task<FlushResult?> AutomaticDrainAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (HasDeliverableAutomaticOpportunity() == false)
                    return null;
            }

            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-checked under the gate, because a manual drain may have captured the authorized work
                // while this waited and may then have left ordinary work queued behind it. The authorization
                // — not the queue alone — decides whether this drain may send anything.
                lock (_gate)
                {
                    if (HasDeliverableAutomaticOpportunity() == false)
                        return null;
                }

                return await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _flushGate.Release();
            }
        }

        // Called under _gate at both edges of one automatic delivery. True only while the client can deliver,
        // work is retained and no other capture has consumed the authorization yet. An opportunity with
        // nothing retained is spent here, which is why an empty signal never touches the network or the Stats
        // snapshot; retained work without an authorization — a manual capture took the Funnel envelope first
        // — waits for a due permission, a new Funnel or an explicit call.
        bool HasDeliverableAutomaticOpportunity()
        {
            if (_state != LifecycleState.Open && _state != LifecycleState.Closing)
            {
                _automaticRequestPending = false;
                return false;
            }

            if (_queue.Count == 0)
            {
                _automaticRequestPending = false;
                return false;
            }

            return _automaticRequestPending;
        }

        // One active registration. It owns the borrowed-source subscription, the single automatic drain
        // and the cancellation of its own work; it owns neither the source, the queue, the transport nor
        // the host callbacks, and it never disposes any of them.
        sealed class AutomaticFlushingController : IDisposable
        {
            internal AutomaticFlushingController(AnalyticsClient client, Action<FlushResult> onResult,
                Action<Exception> onError, double anchorMilliseconds)
            {
                _client = client;
                _onResult = onResult;
                _onError = onError;
                // The enabling context is captured once: observations are dispatched there when the host had
                // one. No dispatch is ever awaited by delivery.
                _callbackContext = SynchronizationContext.Current;
                // The delegate handed to the borrowed source is created once, so subscribing allocates no
                // per-tick closure and an idle permission allocates nothing.
                Opportunity = OnOpportunity;
                _slotAnchorMs = anchorMilliseconds;
                _lastReadingMs = anchorMilliseconds;
                _nextDueMs = anchorMilliseconds + PeriodicIntervalMs;
            }

            // The cached permission callback this registration gives the borrowed source.
            internal Action Opportunity { get; }

            // The caller's opt-out (and the close that stops it): terminal from every state, so it also
            // invalidates a suspension whose observation is still queued. Detaches the borrowed source and
            // cancels automatic work WITHOUT flushing, rewriting an identity or touching the queue: the
            // caller keeps the client, its retained snapshots and manual delivery. Idempotent, and it
            // reports nothing of its own.
            public void Dispose()
            {
                IDisposable? subscription;
                lock (_sync)
                {
                    if (_state == RegistrationState.Disposed)
                        return;

                    _state = RegistrationState.Disposed;
                    subscription = _subscription;
                    _subscription = null;
                }

                _cancellation.Cancel();
                _client.ReleaseAutomaticFlushing(this);
                subscription?.Dispose();
            }

            internal void Attach(IDisposable subscription)
            {
                bool terminal;
                lock (_sync)
                {
                    terminal = _state != RegistrationState.Active;
                    if (terminal == false)
                        _subscription = subscription;
                }

                // A source that signalled before the subscription was attached, or a registration stopped
                // in between, must not stay subscribed.
                if (terminal)
                    subscription.Dispose();
            }

            // Wakes the single automatic drain. It never blocks and never allocates when a drain is already
            // running: the authorizing state lives in the client and is set before this is called, so a
            // wake-up can neither be lost nor produce a second concurrent drain.
            internal void RequestDrain()
            {
                lock (_sync)
                {
                    if (_state != RegistrationState.Active || _drainRunning)
                        return;

                    _drainRunning = true;
                }

                // The task is intentionally not stored: DrainAsync observes every outcome itself, so no
                // faulted or unobserved task can survive a stop.
                Task.Run((Func<Task>)DrainAsync);
            }

            // The borrowed source calls this on an arbitrary thread, once per framework permission. It only
            // samples the scheduling clock and, when a periodic slot is due, authorizes one delivery: no
            // capture, HTTP attempt, host callback or task starts from an idle permission.
            void OnOpportunity()
            {
                double reading;
                try
                {
                    reading = _client.ReadSchedulingMilliseconds();
                }
                catch (Exception error)
                {
                    ReportRejectedReading(error);
                    return;
                }

                var due = false;
                var backward = false;
                lock (_sync)
                {
                    if (_state != RegistrationState.Active)
                        return;

                    // A reading earlier than the previous one would rewind the slot schedule, so it is
                    // refused rather than used to invent a time.
                    if (reading < _lastReadingMs)
                        backward = true;
                    else
                    {
                        _lastReadingMs = reading;
                        if (reading >= _nextDueMs)
                        {
                            // One due permission consumes exactly one slot and skips every missed slot to
                            // the next future boundary, so a late signal never causes catch-up sends.
                            var slots = Math.Floor((reading - _slotAnchorMs) / PeriodicIntervalMs) + 1;
                            _nextDueMs = _slotAnchorMs + (slots * PeriodicIntervalMs);
                            due = true;
                        }
                    }
                }

                if (backward)
                {
                    ReportRejectedReading(new AnalyticsClientException(
                        "the scheduling clock readings decreased; automatic flushing is suspended."));
                    return;
                }

                if (due)
                {
                    _client.AuthorizeAutomaticDelivery();
                    RequestDrain();
                }
            }

            // The single automatic drain loop: at most one delivery in flight, further triggers coalesce
            // into the client's one pending request, and a failure — which leaves no request — never starts
            // another drain by itself. Every outcome is observed exactly once: a result to onResult, an
            // exceptional failure to onError, and this registration's own cancellation to nobody.
            async Task DrainAsync()
            {
                while (true)
                {
                    FlushResult? result;
                    try
                    {
                        result = await _client.AutomaticDrainAsync(_cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        ExitDrain();
                        return;
                    }
                    catch (Exception error)
                    {
                        // An exceptional automatic failure ends this drain instead of looping; the queue and
                        // manual delivery are untouched and only a later trigger starts new automatic work.
                        ExitDrain();
                        ReportError(error);
                        return;
                    }

                    if (result != null)
                        ReportResult(result);

                    // Decided under the registration lock and the client lock together, so a trigger that
                    // arrives exactly as this drain finishes either wakes this loop again or is observed by
                    // the next RequestDrain, never lost in between.
                    lock (_sync)
                    {
                        if (_state != RegistrationState.Active || _client.HasAutomaticRequest() == false)
                        {
                            _drainRunning = false;
                            return;
                        }
                    }
                }
            }

            // Marks the loop finished; any request recorded meanwhile stays recorded, so a later wake-up
            // still sees it.
            void ExitDrain()
            {
                lock (_sync)
                    _drainRunning = false;
            }

            // A rejected scheduling reading suspends automatic delivery and reports it exactly once, and only
            // when this rejection wins the Active -> Suspended transition. An explicit opt-out that already
            // happened — even while the clock was still being sampled — therefore reports nothing, and a
            // suspension whose observation is still queued is dropped if the caller disposes first.
            void ReportRejectedReading(Exception error)
            {
                if (TrySuspend() == false)
                    return;

                Post(() =>
                {
                    // Re-checked at observation time: the caller's opt-out is the last word.
                    if (_state == RegistrationState.Suspended)
                        _onError(error);
                });
            }

            // Moves an Active registration to Suspended once and detaches the borrowed source. False means
            // another transition already won, so the rejection is neither reported nor repeated. The
            // cancellation source is deliberately not disposed: a cancelled drain may still observe its
            // token.
            bool TrySuspend()
            {
                IDisposable? subscription;
                lock (_sync)
                {
                    if (_state != RegistrationState.Active)
                        return false;

                    _state = RegistrationState.Suspended;
                    subscription = _subscription;
                    _subscription = null;
                }

                _cancellation.Cancel();
                _client.ReleaseAutomaticFlushing(this);
                subscription?.Dispose();
                return true;
            }

            // One automatic delivery result. The observation-time guard keeps a stopped registration from
            // reporting stale work, and the host callback always runs outside every SDK lock.
            void ReportResult(FlushResult result)
                => Post(() =>
                {
                    if (_state == RegistrationState.Active)
                        _onResult(result);
                });

            void ReportError(Exception error)
                => Post(() =>
                {
                    if (_state == RegistrationState.Active)
                        _onError(error);
                });

            // Observations leave through the enabling context when the host had one, otherwise through the
            // thread pool, so an invalid host callback faults in the host's own dispatch path instead of
            // being swallowed inside the SDK's delivery task. Neither path is awaited by delivery.
            void Post(Action observation)
            {
                var context = _callbackContext;
                if (context != null)
                {
                    context.Post(state => ((Action)state!).Invoke(), observation);
                    return;
                }

                ThreadPool.QueueUserWorkItem(state => ((Action)state!).Invoke(), observation);
            }

            // The registration lifecycle. Active runs automatic work; Suspended means a rejected scheduling
            // reading stopped it and only its single failure may still be reported; Disposed means the caller
            // opted out (or Close stopped it), which suppresses every observation, including a suspension
            // that is still queued for dispatch.
            enum RegistrationState
            {
                Active,
                Suspended,
                Disposed,
            }

            // The product interval the periodic policy waits for. A due slot additionally needs the
            // runtime's permission, so this is a policy boundary, not a latency or performance claim.
            const double PeriodicIntervalMs = 10_000;

            readonly AnalyticsClient _client;
            readonly Action<FlushResult> _onResult;
            readonly Action<Exception> _onError;
            readonly SynchronizationContext? _callbackContext;
            readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

            // Guards the subscription, the drain flag and the slot state. Never held across an await, an
            // HTTP call or a host callback; where it nests, it owns the client's gate order.
            readonly object _sync = new object();

            // The periodic slot schedule: the enable reading and the next future boundary after the slots
            // already consumed.
            readonly double _slotAnchorMs;
            double _lastReadingMs;
            double _nextDueMs;

            IDisposable? _subscription;
            bool _drainRunning;

            // Written under _sync and read by delivery and callback threads to suppress stale observations.
            volatile RegistrationState _state = RegistrationState.Active;
        }
    }
}
