using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GameAnalytics.Client.Internal;

namespace GameAnalytics.Client
{
    // Public facade of the standalone categorized analytics client. It is the explicit SDK
    // composition boundary: it owns only its private event snapshots, clock anchor and delivery
    // state — no singleton, container or service locator — and it never disposes the caller's
    // HttpClient or any other caller-owned dependency. The automatic-flushing policy lives in the
    // AutomaticFlushing partial file, inside the class, so it reaches the same locked queue and the
    // same serialized delivery instead of adding a second sender or an event bus.
    public sealed partial class AnalyticsClient
    {
        public AnalyticsClient(AnalyticsClientOptions options, HttpClient httpClient)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (httpClient == null)
                throw new ArgumentNullException(nameof(httpClient));

            // Validate and snapshot every option here, so a misconfigured client fails at
            // construction instead of at the first tracked event or request.
            EventRules.ValidateProjectId(options.ProjectId);
            if (string.IsNullOrWhiteSpace(options.BaseUri) || Uri.TryCreate(options.BaseUri, UriKind.Absolute, out _) == false)
                throw new ArgumentException("BaseUri: must be a non-empty absolute URI.", nameof(options));
            if (options.MaxAttempts < 1)
                throw new ArgumentException("MaxAttempts: must be a positive integer (minimum 1, meaning no retry).", nameof(options));

            var context = SnapshotContext(options.Context);
            EventRules.ValidateContext(context);

            _httpClient = httpClient;
            _projectId = options.ProjectId;
            _installationId = options.InstallationId;
            _sessionId = options.SessionId ?? Guid.NewGuid();
            _defaultContext = context;
            _eventClock = new EventClock(options.MonotonicMilliseconds ?? EventClock.DefaultMonotonicMilliseconds);
            _maxAttempts = options.MaxAttempts;
            _delay = options.Delay ?? DefaultDelay;
            _headers = SnapshotHeaders(options.Headers);
            _timeSyncUri = new Uri(options.BaseUri.TrimEnd('/') + "/v1/time-sync", UriKind.Absolute);
            _batchUri = new Uri(options.BaseUri.TrimEnd('/') + "/v1/events/batch", UriKind.Absolute);
        }

        // Immutable pull snapshot of the delivery counters. Counts describe queued/delivered
        // envelopes, never database rows or unique players.
        public DeliveryStats Stats
        {
            get
            {
                lock (_gate)
                    return new DeliveryStats(_totalTracked, _totalDelivered, _queue.Count, _lastStatus, _lastHttpStatusCode);
            }
        }

        // One time-sync attempt, shared by concurrent same-version callers. The release identity is
        // required and pinned for the client's lifetime: a successful initialization is an idempotent
        // no-op for that same version and is refused for any other one, and a failed or cancelled
        // attempt releases its reservation so an explicit later call may retry — with another valid
        // version when no configured context asserts one. Initializing after closing or closing is
        // invalid.
        public async Task InitializeAsync(string clientVersion, CancellationToken cancellationToken = default)
        {
            Task attempt;
            bool ownsAttempt;
            lock (_gate)
            {
                if (_state == LifecycleState.Closing || _state == LifecycleState.Closed)
                    throw new InvalidOperationException("InitializeAsync: the client is closed.");

                // The candidate is validated before any state-dependent comparison, so a null or
                // malformed version is always the caller's argument error and never a silent state
                // change or request.
                EventRules.ValidateClientVersion(clientVersion);

                if (_state == LifecycleState.Open || _state == LifecycleState.Initializing)
                {
                    // An open or pending client keeps the version it reserved: a different one must
                    // neither replace it nor cancel the shared attempt.
                    if (string.Equals(_clientVersion, clientVersion, StringComparison.Ordinal) == false)
                        throw new InvalidOperationException($"InitializeAsync: the client is already bound to version '{_clientVersion}'.");

                    if (_state == LifecycleState.Open)
                        return;

                    // Joining an attempt another caller started: that first caller's token owns the shared
                    // work, this caller's token only bounds its own wait.
                    attempt = _initializationAttempt!;
                    ownsAttempt = false;
                }
                else
                {
                    // A supplied context version is an assertion about the release identity; it must
                    // agree with what this attempt will pin before any side effect happens.
                    if (_defaultContext.AppVersion != null && string.Equals(_defaultContext.AppVersion, clientVersion, StringComparison.Ordinal) == false)
                        throw new ArgumentException($"clientVersion: '{clientVersion}' does not match the AppVersion '{_defaultContext.AppVersion}' configured in AnalyticsClientOptions.Context.", nameof(clientVersion));

                    _clientVersion = clientVersion;
                    _state = LifecycleState.Initializing;
                    attempt = InitializeCoreAsync(cancellationToken);
                    _initializationAttempt = attempt;
                    ownsAttempt = true;
                }
            }

            if (ownsAttempt)
            {
                await attempt.ConfigureAwait(false);
                return;
            }

            await ObserveSharedAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        }

        // The caller's known identity becomes the identity this client stamps on later snapshots. The
        // assignment is local: it validates the open-client contract and the required installation
        // identity, then commits the supplied UUID under the existing gate — no request, clock read,
        // generated UUID or queued event. Re-binding the same user is an idempotent no-op and a
        // different user requires ClearIdentity first.
        public Task BindIdentityAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                try
                {
                    if (_state != LifecycleState.Open)
                        throw new InvalidOperationException("BindIdentityAsync: the client is not open.");
                    EventRules.ValidateIdentity(_installationId);
                    if (_identityState == IdentityState.Bound)
                    {
                        if (_identityUserId != userId)
                            throw new IdentityLinkException("IdentityLinkConflict", "ClearIdentity must be called before binding a different user.");
                        return Task.CompletedTask;
                    }

                    // Checked before the commit, so a cancelled call leaves the client unbound.
                    cancellationToken.ThrowIfCancellationRequested();
                    _identityUserId = userId;
                    _identityState = IdentityState.Bound;
                    return Task.CompletedTask;
                }
                catch (OperationCanceledException)
                {
                    // The caller's own cancellation arrives, as before, as a cancelled awaitable
                    // carrying its token rather than a synchronous throw.
                    return Task.FromCanceled(cancellationToken);
                }
                catch (Exception error)
                {
                    return Task.FromException(error);
                }
            }
        }

        public void ClearIdentity()
        {
            lock (_gate)
                ClearIdentityCore();
        }

        public Guid TrackFunnel(string eventName, Guid? userId = null, Guid? transactionId = null,
            Guid? gameplayAttemptId = null, Guid? downloadId = null,
            IReadOnlyDictionary<string, object?>? properties = null, EventContext? context = null,
            Guid? eventId = null)
            => Track(FunnelCategory, eventName, userId, transactionId, gameplayAttemptId, downloadId, properties, context, eventId);

        public Guid TrackGameplay(string eventName, Guid? userId = null, Guid? transactionId = null,
            Guid? gameplayAttemptId = null, Guid? downloadId = null,
            IReadOnlyDictionary<string, object?>? properties = null, EventContext? context = null,
            Guid? eventId = null)
            => Track(GameplayCategory, eventName, userId, transactionId, gameplayAttemptId, downloadId, properties, context, eventId);

        public Guid TrackEconomy(string eventName, Guid? userId = null, Guid? transactionId = null,
            Guid? gameplayAttemptId = null, Guid? downloadId = null,
            IReadOnlyDictionary<string, object?>? properties = null, EventContext? context = null,
            Guid? eventId = null)
            => Track(EconomyCategory, eventName, userId, transactionId, gameplayAttemptId, downloadId, properties, context, eventId);

        public Guid TrackDiagnostic(string eventName, Guid? userId = null, Guid? transactionId = null,
            Guid? gameplayAttemptId = null, Guid? downloadId = null,
            IReadOnlyDictionary<string, object?>? properties = null, EventContext? context = null,
            Guid? eventId = null)
            => Track(DiagnoseCategory, eventName, userId, transactionId, gameplayAttemptId, downloadId, properties, context, eventId);

        // The active data revision is client-owned state, changeable only while the client is open.
        // Each queued envelope keeps the revision that was active when it was tracked, and a
        // rejected revision leaves the stored one unchanged.
        public void SetActiveDataRevision(string revision)
        {
            lock (_gate)
            {
                if (_state != LifecycleState.Open)
                    throw new InvalidOperationException("SetActiveDataRevision: the client is not open.");

                EventRules.ValidateDataRevision(revision);
                _dataRevision = revision;
            }
        }

        // Drains the work captured at acquisition: batches of at most 500 in input order, each retried
        // inside the attempt budget, stopping at the first batch that cannot be confirmed. Flush calls
        // serialize per client while Track keeps appending concurrently; work tracked after the capture
        // stays queued for a later call. Ordinary delivery failures are results, never thrown callbacks,
        // and user cancellation throws while retaining every unconfirmed envelope.
        public async Task<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                // Only a successfully initialized client can have queued work: before that, Track
                // was rejected. A closed client has nothing left to send. Both are empty results
                // that touch neither the network nor the delivery gate.
                if (_state != LifecycleState.Open && _state != LifecycleState.Closing)
                    return new FlushResult(0, _queue.Count, DeliveryStatus.Complete, _lastHttpStatusCode);
            }

            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _flushGate.Release();
            }
        }

        // Closing blocks new Track synchronously, before anything is awaited. The final flush then
        // drains the retained work, and the client becomes Closed only once that drain confirmed an
        // empty queue; a failed one returns its failure result with the queue retained and the client
        // still Closing, so an explicit repeated Close or Flush retries it. Repeated Close or empty
        // Flush after a successful close is a network-free no-op.
        public async Task<FlushResult> CloseAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_state == LifecycleState.Closed)
                    return new FlushResult(0, _queue.Count, DeliveryStatus.Complete, _lastHttpStatusCode);

                _state = LifecycleState.Closing;
                ClearIdentityCore();
            }

            // Automatic work stops before the existing final serialized drain: the registration detaches
            // from the borrowed source and cancels its own delivery, so nothing automatic competes with
            // this close for the gate. The queue and its identities are untouched by that stop.
            StopAutomaticFlushing();

            var result = await FlushAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == DeliveryStatus.Complete)
            {
                lock (_gate)
                    _state = LifecycleState.Closed;
            }

            return result;
        }

        // Called under _gate. Clearing drops only the local binding and its user; it never rewrites a
        // queued envelope, which keeps the identity snapshot it was tracked with, and never retries
        // delivered bytes.
        void ClearIdentityCore()
        {
            _identityState = IdentityState.Unbound;
            _identityUserId = null;
        }

        Guid? ResolveTrackingIdentity(string category, string eventName, Guid? assertedUserId)
        {
            if (_identityState == IdentityState.Bound)
            {
                if (assertedUserId.HasValue && assertedUserId != _identityUserId)
                    throw new IdentityLinkException("IdentityLinkConflict", "The supplied userId does not match the identity bound by BindIdentityAsync.");
                return _identityUserId;
            }
            if (assertedUserId.HasValue || category != FunnelCategory || EventRules.IsPreLoginEvent(eventName) == false)
                throw new IdentityLinkException("IdentityLinkRequired", "BindIdentityAsync must be called with the caller's known user before user-owned tracking; only the supported anonymous pre-login Funnel events are exempt.");
            return null;
        }

        // One initialization attempt: a finite monotonic sample immediately before exactly one
        // EMPTY POST /v1/time-sync carrying the caller's additional headers, a second sample
        // immediately after the response resolves, then the double-midpoint anchor. Initialization
        // errors stay caller-visible, safe and retryable, and preserve the HTTP status when the
        // attempt reached the server.
        async Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                await InitializeOnceAsync(cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    // A concurrent Close may already have latched the closing transition.
                    if (_state == LifecycleState.Initializing)
                        _state = LifecycleState.Open;
                }
            }
            catch
            {
                lock (_gate)
                {
                    // A failed or cancelled attempt returns the client to Uninitialized so a later
                    // explicit call can retry; no retry loop is started here.
                    if (_state == LifecycleState.Initializing)
                    {
                        _state = LifecycleState.Uninitialized;
                        _initializationAttempt = null;
                        // The reservation is released together with the failed attempt, so an
                        // explicit later call may choose any valid version.
                        _clientVersion = null;
                    }
                }

                throw;
            }
        }

        async Task InitializeOnceAsync(CancellationToken cancellationToken)
        {
            var sendMonotonicMs = _eventClock.ReadMonotonicMilliseconds();

            HttpResponseMessage response;
            try
            {
                // The block bounds the request's lifetime: it is disposed as soon as the response
                // is in hand, before the response body is read.
                using (var request = new HttpRequestMessage(HttpMethod.Post, _timeSyncUri))
                {
                    AddAdditionalHeaders(request);
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException error)
            {
                throw new AnalyticsClientException("InitializeAsync: the time-sync request failed at the transport level.", null, error);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested == false)
            {
                // A transport cancellation the CALLER did not ask for — HttpClient.Timeout elapsing against
                // a stalled server is the documented case, and it arrives as a TaskCanceledException whose
                // own token is already cancelled — is a safe, retryable initialization failure and never
                // the caller's own cancellation. Exactly one attempt per explicit call: no retry loop.
                throw new AnalyticsClientException("InitializeAsync: the time-sync request timed out or was cancelled by the transport.", null, error);
            }

            using (response)
            {
                var receiveMonotonicMs = _eventClock.ReadMonotonicMilliseconds();
                var status = (int)response.StatusCode;

                if (status < 200 || status > 299)
                    throw new AnalyticsClientException($"InitializeAsync: the time-sync endpoint answered HTTP {status}.", status);

                var body = await ReadBodyOrNullAsync(response).ConfigureAwait(false);
                if (body == null)
                    throw new AnalyticsClientException("InitializeAsync: the time-sync response body could not be decoded.", status);

                if (TimeSyncResponse.TryRead(body, out var serverReceivedMs, out var serverSentMs) == false)
                    throw new AnalyticsClientException("InitializeAsync: the time-sync response did not carry exactly the two explicit-UTC server timestamps.", status);
                if (serverSentMs < serverReceivedMs)
                    throw new AnalyticsClientException("InitializeAsync: the server sent earlier than it received.", status);
                if (receiveMonotonicMs < sendMonotonicMs)
                    throw new AnalyticsClientException("InitializeAsync: the response-receive monotonic reading precedes the request-send reading.");

                _eventClock.Anchor(sendMonotonicMs, receiveMonotonicMs, serverReceivedMs, serverSentMs);
            }
        }

        // A caller that joins an in-flight initialization owns only its OWN wait. Its token bounds that wait
        // and never the shared work: cancelling it completes this call with the caller's own
        // OperationCanceledException while the attempt keeps running for its owner and for every other
        // caller. Cancelling here neither cancels nor faults the shared attempt — the losing task of
        // WhenAny is simply left alone — and no Task.WaitAsync (which this target framework lacks) is used.
        static async Task ObserveSharedAttemptAsync(Task attempt, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled == false)
            {
                await attempt.ConfigureAwait(false);
                return;
            }

            // A caller that is already cancelled never joins at all: its own cancellation is what it sees.
            cancellationToken.ThrowIfCancellationRequested();

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                var completed = await Task.WhenAny(attempt, cancelled.Task).ConfigureAwait(false);
                if (completed != attempt)
                    throw new OperationCanceledException(cancellationToken);
            }

            // The attempt settled first: every joiner observes its outcome, exactly as its owner does.
            await attempt.ConfigureAwait(false);
        }

        // The single private tracking implementation behind the four categorized entry points.
        // Track is synchronous and non-blocking apart from this short private lock: no network,
        // retry, disk I/O or JSON serialization happens here. The category is stamped from the
        // caller's entry point and cannot bypass any rule below.
        Guid Track(string category, string eventName, Guid? userId, Guid? transactionId,
            Guid? gameplayAttemptId, Guid? downloadId, IReadOnlyDictionary<string, object?>? properties,
            EventContext? context, Guid? eventId)
        {
            Guid queuedEventId;
            // An accepted Funnel entry requests automatic delivery. The request is recorded in the same
            // locked state the next capture consumes, and the delivery itself starts after the lock.
            AutomaticFlushingController? requestDelivery = null;
            lock (_gate)
            {
                if (_state != LifecycleState.Open)
                    throw new InvalidOperationException("Track: the client is not open; events can only be tracked after InitializeAsync succeeds and before CloseAsync.");

                // Every rule runs BEFORE the event ID, the queue and the clock are touched, so a
                // rejected call can never consume a clock reading, mutate the queue or reach the
                // network.
                EventRules.ValidateEventName(eventName);
                EventRules.ValidateClientAuthority(eventName);
                userId = ResolveTrackingIdentity(category, eventName, userId);
                EventRules.ValidateIdentity(_installationId);
                var eventContext = MergeContext(_defaultContext, context);
                EventRules.ValidateContext(eventContext);

                // A supplied version is only ever an assertion about the release identity the client
                // pinned at initialization; the event-owned snapshot (never the caller's context)
                // always carries that pinned identity, so omitting it still reaches the wire.
                if (eventContext.AppVersion != null && string.Equals(eventContext.AppVersion, _clientVersion, StringComparison.Ordinal) == false)
                    throw new ArgumentException($"context: AppVersion '{eventContext.AppVersion}' does not match the initialized client version '{_clientVersion}'.", nameof(context));
                eventContext.AppVersion = _clientVersion;

                EventRules.ValidateProperties(properties);
                EventRules.ValidateLevelCompleted(eventName, properties);
                EventRules.ValidateCorrelation(eventName, transactionId, gameplayAttemptId, downloadId);

                // A supplied event ID is preserved; otherwise one platform GUID is generated per
                // logical event. Identity and causal IDs are game-owned and never generated.
                queuedEventId = eventId ?? Guid.NewGuid();

                // The one clock read, strictly after every rejection rule.
                var eventTimeUtc = _eventClock.CurrentEventTimeUtc();

                _queue.Enqueue(new EventEnvelope(
                    queuedEventId, eventName, category, eventTimeUtc, _projectId, _installationId, _sessionId,
                    userId, transactionId, gameplayAttemptId, downloadId, eventContext, _dataRevision,
                    SnapshotProperties(properties)));
                _totalTracked++;

                // Only an opted-in client schedules automatic delivery; the request is consumed by the
                // capture that takes this envelope, so it can never outlive its own work.
                if (string.Equals(category, FunnelCategory, StringComparison.Ordinal) && _automaticFlushing != null)
                {
                    AuthorizeAutomaticDelivery();
                    requestDelivery = _automaticFlushing;
                }
            }

            // Outside the gate and never inline: the drain runs on its own task, so tracking a Funnel
            // event starts delivery without waiting for any HTTP work.
            requestDelivery?.RequestDrain();

            return queuedEventId;
        }

        // The full drain of the work captured at acquisition: batches of at most 500, in input order,
        // each retried inside the attempt budget. The drain stops at the first batch that cannot be
        // confirmed and leaves that batch and everything behind it queued — a lost or malformed reply
        // is not proof that the server did not store the events, and the stable event IDs allow a
        // later explicit retry.
        async Task<FlushResult> FlushCoreAsync(CancellationToken cancellationToken)
        {
            List<EventEnvelope> captured;
            lock (_gate)
            {
                if (_queue.Count == 0)
                    return RecordResult(0, DeliveryStatus.Complete, null);

                // The drain target is the work queued at acquisition, in input order. Envelopes
                // appended by later Track calls stay behind it and are reported as pending, so a flush
                // never chases an unbounded stream of future tracking.
                captured = new List<EventEnvelope>(_queue);

                // This capture IS the consumption point for automatic requests, including the immediate
                // Funnel ones (see _automaticRequestPending): it took every envelope that was queued, so a
                // manual capture here leaves nothing for an automatic drain to repeat. A Funnel tracked
                // after this point keeps its own request and authorizes a follow-up delivery.
                _automaticRequestPending = false;
            }

            var delivered = 0;
            var offset = 0;
            int? lastHttpStatus = null;

            while (offset < captured.Count)
            {
                var length = Math.Min(MaxBatchEvents, captured.Count - offset);
                var delivery = await DeliverChunkAsync(captured, offset, length, cancellationToken).ConfigureAwait(false);
                lastHttpStatus = delivery.HttpStatusCode;

                if (delivery.Failure.HasValue)
                    return RecordResult(delivered, delivery.Failure.Value, delivery.HttpStatusCode);

                // Only a confirmed batch is removed, and the removal is atomic with the counters, so
                // the reported numbers always describe what really happened. A cancellation between
                // batches therefore leaves every confirmed removal and count standing.
                lock (_gate)
                {
                    for (var i = 0; i < length; i++)
                        _queue.Dequeue();
                    _totalDelivered += length;
                }

                delivered += length;
                offset += length;
            }

            int pending;
            DeliveryStatus status;
            lock (_gate)
            {
                pending = _queue.Count;
                status = pending == 0 ? DeliveryStatus.Complete : DeliveryStatus.Pending;
                _lastStatus = status;
                _lastHttpStatusCode = lastHttpStatus;
            }

            return new FlushResult(delivered, pending, status, lastHttpStatus);
        }

        // One batch's delivery loop. The bytes are serialized ONCE here, off the calling thread, and
        // every attempt wraps those same bytes in a fresh request, so a retry is byte-identical and
        // keeps the stable event IDs. Network failures and HTTP 408/429/5xx retry within the configured
        // attempt budget using the source-backed capped exponential backoff; a permanent HTTP status or
        // an acknowledgement that cannot be confirmed stops immediately.
        async Task<ChunkDelivery> DeliverChunkAsync(
            IReadOnlyList<EventEnvelope> captured,
            int offset,
            int length,
            CancellationToken cancellationToken)
        {
            var body = BatchJson.SerializeBatch(captured, offset, length);
            var attempts = 0;

            while (true)
            {
                attempts++;

                var response = await TrySendBatchAsync(body, cancellationToken).ConfigureAwait(false);
                if (response == null)
                {
                    // Transport-level failure: retryable, and carrying no HTTP status.
                    if (attempts >= _maxAttempts)
                        return ChunkDelivery.Failed(DeliveryStatus.NetworkFailure, null);

                    await BackoffAsync(attempts - 1, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    var status = (int)response.StatusCode;

                    if (status >= 200 && status <= 299)
                    {
                        var payload = await ReadBodyOrNullAsync(response).ConfigureAwait(false);
                        if (payload == null
                            || BatchJson.TryReadAcknowledgement(payload, out var accepted, out var eventIds) == false
                            || AcknowledgementMatches(accepted, eventIds, captured, offset, length) == false)
                            return ChunkDelivery.Failed(DeliveryStatus.InvalidResponse, status);

                        return ChunkDelivery.Confirmed(status);
                    }

                    if (IsRetryableStatus(status) == false || attempts >= _maxAttempts)
                        return ChunkDelivery.Failed(DeliveryStatus.HttpFailure, status);
                }

                await BackoffAsync(attempts - 1, cancellationToken).ConfigureAwait(false);
            }
        }

        // Exactly one POST attempt. A transport-level failure is reported as null so the caller treats it
        // as the ordinary retryable delivery failure it is: either a thrown HttpRequestException, or a
        // transport cancellation the CALLER did not ask for (HttpClient.Timeout elapsing is the documented
        // case, and it arrives as a TaskCanceledException even though the caller's token was never
        // cancelled). Cancellation of the caller's OWN token is never intercepted here — it propagates, so
        // the caller sees their own OperationCanceledException and every unconfirmed envelope stays queued.
        async Task<HttpResponseMessage?> TrySendBatchAsync(byte[] body, CancellationToken cancellationToken)
        {
            try
            {
                return await SendBatchAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
            {
                return null;
            }
        }

        // Reads a buffered response body as text. A response whose declared character set this runtime
        // cannot decode cannot be read at all; that is a malformed response rather than a transport
        // failure, so it is reported as null and the caller decides (an unconfirmable acknowledgement for a
        // batch, a malformed time-sync response for initialization). Nothing else is intercepted here: the
        // body is already buffered, so ordinary body/network errors surface inside SendAsync.
        static async Task<string?> ReadBodyOrNullAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // Exactly one POST of the batch bytes. A fresh request and content wrapper around the same
        // immutable bytes is exactly what makes the next attempt byte-identical.
        async Task<HttpResponseMessage> SendBatchAsync(byte[] body, CancellationToken cancellationToken)
        {
            // The block bounds the request's lifetime: it is disposed as soon as the fully buffered
            // response is in hand, before the acknowledgement is read.
            using (var request = new HttpRequestMessage(HttpMethod.Post, _batchUri))
            {
                // The JSON media type is owned by the client, so a caller-supplied variant can never
                // reach the wire.
                // Transfer ownership immediately, so header setup failures also dispose the content.
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                AddAdditionalHeaders(request);
                return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        // The source-backed capped exponential backoff shared with the JavaScript client:
        // 500 ms * 2^retriesDone, capped at 30 s. The wait is the caller-supplied cancellable delay
        // operation, so the SDK itself never owns a sleep.
        Task BackoffAsync(int retriesDone, CancellationToken cancellationToken)
        {
            var delayMs = Math.Min(RetryDelayBaseMs * Math.Pow(2, retriesDone), RetryDelayMaxMs);
            return _delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
        }

        // 408 and 429 are transient by protocol and 5xx is a server-side failure; every other status is
        // permanent and must not consume the retry budget.
        static bool IsRetryableStatus(int status)
            => status == 408 || status == 429 || (status >= 500 && status <= 599);

        // A successful acknowledgement must confirm exactly this batch: the accepted count and the
        // returned event IDs, in request order. Anything else leaves the batch (and everything behind
        // it) queued.
        static bool AcknowledgementMatches(int accepted, IReadOnlyList<Guid> eventIds, IReadOnlyList<EventEnvelope> captured, int offset, int length)
        {
            if (accepted != length || eventIds.Count != length)
                return false;

            for (var i = 0; i < length; i++)
            {
                if (eventIds[i] != captured[offset + i].EventId)
                    return false;
            }

            return true;
        }

        FlushResult RecordResult(int delivered, DeliveryStatus status, int? httpStatus)
        {
            int pending;
            lock (_gate)
            {
                pending = _queue.Count;
                _lastStatus = status;
                _lastHttpStatusCode = httpStatus;
            }

            return new FlushResult(delivered, pending, status, httpStatus);
        }

        void AddAdditionalHeaders(HttpRequestMessage request)
        {
            foreach (var header in _headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // The effective per-event context: the options defaults, with each field the caller
        // supplied for this event overriding the corresponding default. The result is a fresh
        // instance owned by the queued envelope, so mutating either source afterwards is invisible.
        static EventContext MergeContext(EventContext defaults, EventContext? overrides)
        {
            if (overrides == null)
                return SnapshotContext(defaults);

            return new EventContext
            {
                Platform = overrides.Platform ?? defaults.Platform,
                DeviceClass = overrides.DeviceClass ?? defaults.DeviceClass,
                AppVersion = overrides.AppVersion ?? defaults.AppVersion,
                BuildId = overrides.BuildId ?? defaults.BuildId,
                Country = overrides.Country ?? defaults.Country,
                Region = overrides.Region ?? defaults.Region,
                Timezone = overrides.Timezone ?? defaults.Timezone,
                Locale = overrides.Locale ?? defaults.Locale,
            };
        }

        static EventContext SnapshotContext(EventContext? source)
        {
            if (source == null)
                return new EventContext();

            return new EventContext
            {
                Platform = source.Platform,
                DeviceClass = source.DeviceClass,
                AppVersion = source.AppVersion,
                BuildId = source.BuildId,
                Country = source.Country,
                Region = source.Region,
                Timezone = source.Timezone,
                Locale = source.Locale,
            };
        }

        // A true snapshot of the flat scalar values: the queued envelope never aliases the caller's
        // dictionary, so mutating it afterwards cannot rewrite what will be delivered.
        static IReadOnlyList<KeyValuePair<string, object?>>? SnapshotProperties(IReadOnlyDictionary<string, object?>? properties)
        {
            if (properties == null)
                return null;

            var snapshot = new List<KeyValuePair<string, object?>>(properties.Count);
            foreach (var property in properties)
                snapshot.Add(property);

            return snapshot;
        }

        // Construction snapshot of the caller's headers. A caller-supplied Content-Type is dropped
        // rather than refused: the batch route's JSON media type is owned by the client and must
        // survive any differently cased caller variant.
        static IReadOnlyList<KeyValuePair<string, string>> SnapshotHeaders(IReadOnlyDictionary<string, string>? headers)
        {
            var snapshot = new List<KeyValuePair<string, string>>();
            if (headers == null)
                return snapshot;

            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key))
                    throw new ArgumentException("Headers: header names must be non-empty.", nameof(headers));
                if (header.Value == null)
                    throw new ArgumentException($"Headers: header '{header.Key}' must have a non-null value.", nameof(headers));
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;

                snapshot.Add(new KeyValuePair<string, string>(header.Key, header.Value));
            }

            return snapshot;
        }

        // The default retry delay, cancellable and never a busy wait.
        static Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken)
            => Task.Delay(delay, cancellationToken);

        // One batch's delivery outcome: Failure is null when the batch was confirmed, otherwise it
        // carries the status that stops the drain, with the last HTTP status that was observed.
        readonly struct ChunkDelivery
        {
            ChunkDelivery(DeliveryStatus? failure, int? httpStatusCode)
            {
                Failure = failure;
                HttpStatusCode = httpStatusCode;
            }

            public DeliveryStatus? Failure { get; }

            public int? HttpStatusCode { get; }

            public static ChunkDelivery Confirmed(int httpStatusCode) => new ChunkDelivery(null, httpStatusCode);

            public static ChunkDelivery Failed(DeliveryStatus failure, int? httpStatusCode) => new ChunkDelivery(failure, httpStatusCode);
        }

        // The four approved categories of the frozen wire contract; each public entry point stamps
        // exactly one.
        const string FunnelCategory = "funnel";
        const string GameplayCategory = "gameplay";
        const string EconomyCategory = "economy";
        const string DiagnoseCategory = "diagnose";

        // The batch endpoint accepts 1..500 envelopes per request.
        const int MaxBatchEvents = 500;

        // The source-backed retry schedule shared with the JavaScript client: 500 ms doubling per
        // retry already done, capped at 30 s.
        const double RetryDelayBaseMs = 500;
        const double RetryDelayMaxMs = 30_000;

        enum IdentityState { Unbound, Bound }

        readonly HttpClient _httpClient;
        readonly string _projectId;
        readonly Guid? _installationId;
        readonly Guid _sessionId;
        readonly EventContext _defaultContext;
        readonly EventClock _eventClock;
        readonly IReadOnlyList<KeyValuePair<string, string>> _headers;
        readonly Uri _timeSyncUri;
        readonly Uri _batchUri;

        // The per-batch attempt budget and the cancellable inter-attempt delay that the delivery loop
        // consults; both were validated and snapshotted at construction.
        readonly int _maxAttempts;
        readonly Func<TimeSpan, CancellationToken, Task> _delay;

        // Guards the lifecycle state, the queue, the active revision, the counters and the clock
        // anchor. Never held across an await, HTTP call, serialization or backoff.
        readonly object _gate = new object();

        // Serializes Flush/Close delivery per client so the queue is never double-sent; Track never
        // takes it.
        readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);

        // Input order; the only delivery state this client owns.
        readonly Queue<EventEnvelope> _queue = new Queue<EventEnvelope>();

        // The one active automatic registration, or null while automatic flushing is off. Guarded by
        // _gate; the registration itself owns the borrowed source subscription and its drain.
        AutomaticFlushingController? _automaticFlushing;

        // An automatic delivery is authorized and not yet consumed: an accepted Funnel track asks for it
        // immediately, and a due runtime permission grants it periodically. It is consumed by the same
        // locked queue capture that takes the envelopes, including a manual capture, so no automatic drain
        // can repeat work that another capture already delivered.
        bool _automaticRequestPending;

        LifecycleState _state = LifecycleState.Uninitialized;
        Task? _initializationAttempt;

        // The pinned release identity: reserved under _gate before an attempt starts, retained while
        // the client is open, and cleared with a failed or cancelled attempt.
        string? _clientVersion;
        string? _dataRevision;
        IdentityState _identityState;
        Guid? _identityUserId;
        long _totalTracked;
        long _totalDelivered;
        DeliveryStatus _lastStatus = DeliveryStatus.Pending;
        int? _lastHttpStatusCode;
    }
}
