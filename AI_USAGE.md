# PlayLens C# SDK: instructions for AI-assisted integration

Read this before generating integration code for any C# project. Reference the
`GameAnalytics.Client` library and use its public APIs, not guessed authentication,
registration or lifecycle methods. Assembly and namespace are `GameAnalytics.Client`.
No application framework is assumed. The standalone library targets .NET Standard
2.0 and depends on System.Text.Json; resolve compatible dependencies through the
library's build/distribution. Installation details belong in its README.

## Responsibilities

- PlayLens is telemetry, **not authentication**. It does not create accounts,
  register guests, promote users, manage account tiers or provide an identity service.
  Never add any of those as prerequisites to analytics.
- The host supplies the Ready project's exact ID, reachable API base URL and
  client write key in `Headers["X-Api-Key"]`. Initialization is time synchronization,
  not proof of key validity. Inspect delivery results. Never embed the separate
  backend/server secret, owner tokens or dashboard credentials in a client.
- The host creates and persists one installation UUID across launches. Supply a
  launch session UUID when it must correlate with other systems; otherwise the
  SDK creates one per client. User UUIDs come from the application's identity system.
- One `AnalyticsClient` owns its clock, binding, in-memory queue, retry policy and
  lifecycle. The host owns the borrowed `HttpClient`, consent, startup admissions
  and runtime delivery opportunities. The SDK never disposes the HTTP client.
- This library has no singleton, application lifecycle hooks, disk queue,
  automatic exception capture or consent UI. Compose it through existing host
  ownership; do not invent parallel application or delivery infrastructure.

## Expected application flow

1. Apply the host's collection and consent policy before collecting or submitting
   analytics.
2. Construct the client with host-owned configuration and installation/session
   IDs. Initialize asynchronously with the application's canonical release version
   and bounded cancellation. The analytics owner must await initialization before
   using the client; business operations and successful login must not depend on
   telemetry availability. If startup need not wait for analytics, observe its
   initialization in the background rather than blocking the primary workflow.
3. The SDK cannot Track or Bind until `InitializeAsync` succeeds. If early signals
   must survive startup, retain them in the existing host analytics boundary with
   their original properties and identity snapshots. This is not an SDK disk queue.
4. Once initialized, track actual anonymous pre-login Funnel events. When the host
   knows the user UUID, call `BindIdentityAsync(userId)` before later user-owned
   Track calls. Binding is **local**, with no HTTP request, backend evidence,
   confirmation endpoint or account-registration requirement. Observe its Task.
5. Replay retained signals in order using their captured identities, including
   null before login. Never stamp the latest user onto old events. Restore the
   latest desired identity after replay. Retire old consent generations so late
   initialization cannot publish a stale client or replay opted-out work.
6. Deliver asynchronously after initialization. Waiting for analytics readiness
   belongs inside the analytics owner, not the primary application workflow. Keep
   any other telemetry providers independent: PlayLens failure must not delay
   their identity assignment or event recording, or turn successful login into
   failed login.

The following illustrates calls **inside the host's analytics flow**, not a login
handler. All identifiers, configuration, HTTP client and bounded cancellation
token are supplied by the host. An executable or batch host must keep this flow
alive until its required delivery and shutdown work completes:

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

## Pre-login, post-login and stable identity

### Pre-login versus post-login

- **Pre-login:** the host knows the installation and session, but has no user UUID.
  Leave the client unbound. Only the anonymous Funnel allowlist below is accepted;
  do not invent a temporary user, send an empty UUID as an anonymous marker, or
  bypass the restriction with a per-event `userId` argument.
- **Post-login:** after the host's own identity system supplies the real user UUID,
  await local `BindIdentityAsync(userId)` inside the analytics owner before tracking
  user-owned events. Normally omit the optional per-event `userId`: the binding
  supplies it. The SDK does not authenticate the user or verify an account record.
  No backend login event or remote confirmation is needed to bind.
- **Already signed in:** bind the existing host-known UUID after SDK initialization;
  do not manufacture a new pre-login journey or replay milestones just because a
  new client/session starts. Record only actions that actually occurred.
- **Logout:** `ClearIdentity()` makes subsequent tracking unbound again. It does
  not delete queued events. Bind the next actual user before user-owned tracking;
  a failed application login is not a reason to assign another analytics identity.

For example, an anonymous `app_started` tracked under installation I/session S
keeps `(I, S, null)`. After binding U, later accepted events carry `(I, S, U)`.
Even if both are delivered in one batch after login, the earlier event stays null.
Query-time attribution of an anonymous prefix is separate from the SDK's stored
snapshots: the server can use one unambiguous client user for the same exact
project/installation/session, but must not guess between conflicting users or
rewrite raw event identities.

### Which identifiers stay stable

| Identifier | Host/SDK responsibility and lifetime |
| --- | --- |
| `projectId` | Host configuration: the exact target project, unchanged for that client. It is not a user or session ID. |
| `installationId` | Host-created UUID persisted across launches until reinstall or local-data reset. Do not generate it per login, event or flush. The current contract requires a non-null installation. |
| `SessionId` | One UUID for the application launch/session. The SDK creates one per client if omitted; supply the host's session ID when sharing context or recreating a client within that session. Do not regenerate it for each event or identity bind. |
| `userId` | Stable UUID from the application's identity system. The same user keeps it across sessions and installations. Guest-to-registered continuation keeps that same UUID; an actually different account has a different UUID. Never derive it from email, generate it on every login, or let analytics decide account merges. |
| `eventId` | One ID per logical event envelope. Track creates it unless supplied; SDK retries preserve both its ID and body bytes. Retry pending delivery with `FlushAsync`, not another Track call. Never reuse an ID for a different event or changed payload. |
| `transactionId`, `gameplayAttemptId`, `downloadId` | Host-owned causal IDs. Related events for one purchase, attempt or download share the corresponding ID; a genuinely new operation gets a new one. These are not replacements for user/session identity. |

The SDK does not persist host identifiers or recover an account after reinstall.
The host must load its installation identity and obtain its existing user UUID
from its own identity system. Client-owned attribution is not authentication or
proof that a client-supplied UUID is trustworthy.

### Binding and snapshot invariants

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

## Choosing the event category

| Method / wire category | Use it for | Meaning |
| --- | --- | --- |
| `TrackFunnel` / `funnel` | User-journey milestones such as application entry, login presentation or reaching an onboarding step. | Evidence for ordered progression and drop-off. Custom funnel charts consider this category, not every recorded event. A milestone records what actually happened, not an assumed later outcome. |
| `TrackGameplay` / `gameplay` | Detailed activity within an interaction or run: starting/ending an attempt and individual actions or outcomes. | Behavioral detail, not automatically a funnel stage. Repeated actions may be useful here without implying new users or restarted onboarding. |
| `TrackDiagnostic` / `diagnose` | Operational observations such as explicit application errors, heartbeats or performance measurements. | Application health and troubleshooting, not user progression or successful business outcomes. The SDK does not automatically capture exceptions. |
| `TrackEconomy` / `economy` | Purchase-flow and other economic activity allowed for a client. | Economic behavior, not diagnostics or funnel progression. Backend-authoritative purchase/reward verdicts remain forbidden to this client. |

All four methods are synchronous and return event `Guid`s; every event has
`source=client`. **The chosen method sets the category.** Names do not infer it:
`TrackDiagnostic("login_presented")` is not a Funnel event and does not bypass
the unbound-tracking rules. Choose actual actions, not invented funnel stages.

If one action needs both detailed activity and a Funnel milestone, deliberately
record two separate event envelopes with different event IDs and the appropriate
shared causal IDs. Do not silently relabel Gameplay or Diagnose events to make
them appear in a funnel.

Category also affects **opted-in automatic delivery**, not identity: an accepted
Funnel event requests immediate asynchronous delivery of the whole captured queue;
Gameplay, Economy and Diagnose require the due slot plus a host opportunity.
In default manual mode all categories stay queued until an explicit Flush/Close.
An immediate request is not a successful-delivery acknowledgment. Never choose
the wrong category just to force a send; delivery belongs to the analytics owner.

### Event inputs and validation

- Optional inputs are causal `transactionId`, `gameplayAttemptId`, `downloadId`,
  flat scalar `properties`, `EventContext` and a stable retry-safe `eventId`.
  Purchase-family names require transactionId; gameplay_started/gameplay_ended
  require gameplayAttemptId; content_download* requires downloadId.
  `level_completed` requires integer `properties.level` from 1 through 200.
- Properties permit string, finite number, boolean and null, not nested objects,
  arrays or arbitrary object instances. At most 32 properties, 64 characters per
  key and 256 per string value. Event names are nonblank and at most 64 characters.
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
  `FlushAsync` from the existing analytics owner, not the application's critical path.
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
  must not call back inline. Do not invent additional SDK scheduling methods.
- In automatic mode an accepted Funnel event requests immediate asynchronous
  **whole-queue** delivery, without runtime permission. Other categories require
  both a due 10-second slot and a runtime permission signal. Slots anchor at enable,
  skip missed intervals and do not reset on immediate/manual delivery. A retained
  failure alone must not spin retries; no permission means ordinary work waits.
- Both automatic callbacks are required and must not throw. They use the enabling
  SynchronizationContext when present, otherwise a thread-pool callback. Marshal
  thread-affine host operations to their owning context. Invalid/backward
  scheduling clock readings suspend automatic delivery and report an error.
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
Compilation does not prove actual delivery, external account promotion, a completed
live funnel or compatibility with every target runtime. Do not send LIVE events
just to validate documentation. Do not publish real keys, application/server
configuration, captured events, build outputs or test data with this library.
