# PlayLens SDK: instructions for AI-assisted integration

Read this before generating integration code. Use the public APIs in `Runtime/`,
not guessed authentication, registration or Unity lifecycle methods. Assembly and
namespace are `GameAnalytics.Client`. This source is shared with the standalone
.NET SDK; keep client behavior equivalent across integrations.

## Responsibilities

- PlayLens is telemetry, **not authentication**. It does not create accounts,
  register guests, promote users, manage account tiers or integrate Supabase.
  Never add any of those as prerequisites to analytics.
- The host supplies the Ready project's exact ID, reachable API base URL and
  client write key in `Headers["X-Api-Key"]`. Initialization is time synchronization,
  not proof of key validity. Inspect delivery results. Never embed the separate
  backend/server secret, owner tokens or dashboard credentials in a client.
- The host creates and persists one installation UUID across launches. Supply a
  launch session UUID when it must correlate with other systems; otherwise the
  SDK creates one per client. Player UUIDs come from the game's identity system.
- One `AnalyticsClient` owns its clock, binding, in-memory queue, retry policy and
  lifecycle. The host owns the borrowed `HttpClient`, consent, startup admissions
  and runtime delivery opportunities. The SDK never disposes the HTTP client.
- This package has no MonoBehaviour, singleton, player-loop hook, disk queue,
  automatic exception capture or consent UI. Compose it through existing host
  ownership; do not invent parallel gameplay or delivery infrastructure.

## Expected flow: initialization does not gate game entry

1. Apply the game's consent policy before collecting or submitting analytics.
2. Construct the client with host-owned configuration and installation/session
   IDs. Start an **observed, bounded background initialization** with the game's
   canonical release version. Do not await analytics from login or game entry.
3. The SDK cannot Track or Bind until `InitializeAsync` succeeds. If early signals
   must survive startup, retain them in the existing host analytics boundary with
   their original properties and identity snapshots. This is not an SDK disk queue.
4. Once initialized, track actual anonymous pre-login Funnel events. When the host
   knows the player UUID, call `BindIdentityAsync(userId)` before later user-owned
   Track calls. Binding is **local**, with no HTTP request, backend evidence,
   confirmation endpoint or account-registration requirement. Observe its Task.
5. Replay retained signals in order using their captured identities, including
   null before login. Never stamp the latest user onto old events. Restore the
   latest desired identity after replay. Retire old consent generations so late
   initialization cannot publish a stale client or replay opted-out work.
6. Deliver asynchronously after initialization. Waiting for readiness belongs
   inside analytics, never in the player's entry path. Keep Unity Analytics and
   other providers independent: PlayLens failure must not delay their identity
   assignment or event recording, and must not turn successful game login into
   a failed login.

The following illustrates calls **inside the host's background analytics flow**,
not a login handler. All identifiers, configuration, HTTP client and bounded
cancellation token are supplied by the host:

```csharp
using System.Collections.Generic;
using GameAnalytics.Client;

var client = new AnalyticsClient(
    new AnalyticsClientOptions(baseUri, projectId, installationId)
    {
        SessionId = sessionId,
        Headers = new Dictionary<string, string> { ["X-Api-Key"] = clientWriteKey }
    }, httpClient);

await client.InitializeAsync("1.4.0", cancellationToken);
client.TrackFunnel("app_started"); // Only if this action actually occurred; user stays null.

await client.BindIdentityAsync(userId, cancellationToken); // Local assignment, not authentication.
client.TrackGameplay("gameplay_started", gameplayAttemptId: attemptId);
client.TrackEconomy("purchase_started", transactionId: transactionId);
client.TrackDiagnostic("heartbeat");

var result = await client.FlushAsync(cancellationToken);
// Inspect Status, Delivered, Pending and LastHttpStatusCode; Task completion is not delivery proof.
```

## Identity and event contracts

- `BindIdentityAsync(Guid, CancellationToken)` requires an initialized Open client
  and non-null installation ID. Same bound UUID is an idempotent no-op. A different
  user requires `ClearIdentity()` first. Binding does not generate an event or ID.
- `ClearIdentity()` is local and idempotent. Logout/account switch changes only
  subsequent tracking. Binding, clearing, flushing and closing never rewrite the
  identities of queued events. Anonymous snapshots stay null; known-user snapshots
  keep their original UUID even if delivered after a switch.
- A guest continuing as a registered account with the **same UUID** stays the same
  user. Do not recreate analytics identity, restart the funnel, replay startup or
  emit another `new_player`. External identity promotion is the host's job.
- Unbound tracking permits only Funnel names `app_started`, `config_fetch_started`,
  `config_fetch_completed`, `config_fetch_failed`, `login_presented`,
  `terms_accepted`, `login_started`, `login_failed`, `login_cancelled`, without a
  supplied user. Other unbound tracking throws `IdentityLinkRequired`.
- Optional Track `userId` is a checked assertion of the **locally bound** user,
  not a way to assign one. A mismatch or binding another user without clearing
  throws `IdentityLinkConflict`. The exception type retains its historical name
  `IdentityLinkException`; do not infer a network confirmation protocol from it.
- `TrackFunnel`, `TrackGameplay`, `TrackEconomy`, `TrackDiagnostic` are synchronous
  and return event `Guid`s. They map to wire categories `funnel`, `gameplay`,
  `economy`, `diagnose`, respectively; every event has `source=client`. Category is
  not inferred from a name. Choose actual actions, not invented funnel stages.
- Optional inputs are causal `transactionId`, `gameplayAttemptId`, `downloadId`,
  flat scalar `properties`, `EventContext` and a stable retry-safe `eventId`.
  Purchase-family names require transactionId; gameplay_started/gameplay_ended
  require gameplayAttemptId; content_download* requires downloadId.
  `level_completed` requires integer `properties.level` from 1 through 200.
- Properties permit string, finite number, boolean and null, not nested objects,
  arrays or Unity objects. At most 32 properties, 64 characters per key and 256
  per string value. Event names are nonblank and at most 64 characters.
- Never set event time/source/category manually. SDK-owned synchronized time,
  identity, IDs, properties, context and data revision are snapshotted at Track.
- Initialize with exactly three numeric version components, no leading zero
  except `0`, maximum 32 characters, no whitespace/prefix/suffix. No default version
  or setter exists. The version is fixed for that instance; `EventContext.AppVersion`
  can only assert equality. `SetActiveDataRevision(revision)` affects future events
  after the host actually activates that data, independently of the app version.
- Clients cannot emit backend verdicts `login_succeeded`, `new_player`,
  `purchase_validated`, `purchase_rejected`, `reward_granted`, `reward_failed`.
  Trusted backend reporting is separate and never required to locally bind a user.
  There is no backend sender in this package.

## Delivery, consent and shutdown

- Manual delivery is default. Track queues without waiting for delivery. Call
  `FlushAsync` from the existing analytics owner, not gameplay's critical path.
  Flush serializes per client, captures current work and batches at most 500 in
  order; newer events may remain for another flush.
- Retries preserve body bytes and event IDs. Only matching acknowledgments remove
  queued work. Network/transport timeouts and HTTP 408/429/5xx retry within
  `MaxAttempts` (default 3) with capped backoff. A lost response does not prove
  nothing was stored. Caller cancellation throws and retains unconfirmed work.
- Ordinary delivery failures return `FlushResult`: Complete, Pending,
  NetworkFailure, HttpFailure or InvalidResponse. Inspect counts/status and report
  failure safely, without credentials or response bodies. `Stats` is an immutable
  pull snapshot of envelope counts, not database rows or unique players.
- Optional `StartAutomaticFlushing(source, onResult, onError)` after initialization
  returns one `IDisposable` registration. The host-supplied
  `IFlushOpportunitySource.Subscribe(Action)` returns a detaching subscription and
  must not call back inline. Do not invent EndFrameAsync or per-frame SDK driving.
- In automatic mode an accepted Funnel event requests immediate asynchronous
  **whole-queue** delivery, without runtime permission. Other categories require
  both a due 10-second slot and a runtime permission signal. Slots anchor at enable,
  skip missed intervals and do not reset on immediate/manual delivery. A retained
  failure alone must not spin retries; no permission means ordinary work waits.
- Both automatic callbacks are required and must not throw. They use the enabling
  SynchronizationContext when present, otherwise a thread-pool callback. Do not
  use Unity APIs on arbitrary callback threads. Invalid/backward scheduling clock
  readings suspend automatic delivery and report an error.
- Consent opt-out must stop host admissions/retention, cancel the retired consent
  generation and dispose automatic registration **without Close/Flush**. Disposal
  cancels scheduling but leaves that client's queue intact; abandon the client
  according to host policy and never replay it under renewed consent. A submitted
  request cannot be recalled. Never flush as a side effect of withdrawing consent.
- At ordinary permitted shutdown, await bounded `CloseAsync` and inspect its
  result. It stops automatic scheduling and new Track immediately, then drains.
  A failed close remains Closing for explicit delivery retry. Keep the host-owned
  HTTP client alive while required retries remain. Process exit loses the memory
  queue: do not promise zero loss or persistence.

## Verification boundaries

Use host doubles or an isolated owned endpoint for integration verification.
Compilation does not prove actual delivery, external account promotion, a live
funnel, IL2CPP/stripping compatibility or device support. Never enter Play or send
LIVE events just to generate metadata. Do not publish real keys, server/game
configuration, captured events, build outputs or test data with this package.
