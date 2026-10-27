---
name: playlens-csharp-sdk
description: Install and integrate the PlayLens C# SDK (Unity package or .NET Standard library), then use the PlayLens MCP connection to configure pre-login events and create funnels. Use when adding PlayLens analytics to a C#/Unity game or when asked to manage PlayLens funnels or pre-login events from an AI agent.
---

# PlayLens C# SDK + MCP

PlayLens is telemetry, not authentication: it never creates accounts, registers guests or
promotes users. Namespace and assembly: `GameAnalytics.Client`.

## Install

**Unity (6000.6+)** — add the package by git URL in the Package Manager:

```
https://github.com/sebas77/PlayLensAI-Unity.git
```

The package is `com.playlensai.sdk`, assembly `GameAnalytics.Client`, `autoReferenced: false`
(reference it from your assembly definition). It requires Unity's supplied System.Text.Json.

**Standalone .NET** — reference `csharp-sdk/src/GameAnalytics.Client/GameAnalytics.Client.csproj`
(netstandard2.0, System.Text.Json). The library has no Unity, database or server dependency; the
host owns `HttpClient`.

## Client integration

```csharp
using GameAnalytics.Client;

var client = new AnalyticsClient(
    new AnalyticsClientOptions(baseUri, projectId, installationId)
    {
        SessionId = sessionId,                                  // one per launch
        Headers = new Dictionary<string, string> { ["X-Api-Key"] = clientWriteKey }
    }, httpClient);

await client.InitializeAsync("1.4.0", cancellationToken);
client.TrackFunnel("app_started");
await client.BindIdentityAsync(userId, cancellationToken);     // local assignment
client.TrackGameplay("gameplay_started", gameplayAttemptId: attemptId);
client.TrackEconomy("purchase_started", transactionId: transactionId);
client.TrackDiagnostic("heartbeat");

var result = await client.FlushAsync(cancellationToken);
// result.Status / Delivered / Rejected / Pending / LastHttpStatusCode
await client.CloseAsync(cancellationToken);
```

Rules that matter:

- **Pre-login events are server-owned.** Before `BindIdentityAsync`, only Funnel events may be
  tracked, and the server accepts them only if the name is in the project's installation-scoped
  list. The list is empty for a new project: configure it before shipping.
- A refused name does not fail the whole batch. The server stores the rest and reports rejected
  indexes; the SDK removes those envelopes and counts them in `FlushResult.Rejected` and
  `DeliveryStats.TotalRejected`.
- Binding is local and idempotent for the same user; another user needs `ClearIdentity()` first.
- One method per category: `TrackFunnel`, `TrackGameplay`, `TrackEconomy`, `TrackDiagnostic`.
  Clients cannot emit `login_succeeded`, `new_player`, `purchase_validated`, `purchase_rejected`,
  `reward_granted`, `reward_failed`.
- Host startup must not block game entry or login on PlayLens. Do not dispose the host HttpClient
  while retries remain.

## Connect the MCP

The MCP endpoint is the analytics host with `/mcp`, authorized by the workspace owner's OAuth
session.

```json
{
  "mcp": {
    "playlens": {
      "type": "remote",
      "url": "https://your-analytics-host/mcp",
      "enabled": true
    }
  }
}
```

Scopes:

- `playlens:connect` — read tools (`connection_status`, `funnel_events`, `list_funnels`,
  `list_installation_events`).
- `playlens:funnels:write` — write tools (`create_funnel`, `set_installation_events`), also
  requiring `team_admin` membership.

Re-authorize after a scope change. Never paste an access token into source or logs.

## Tools

| Tool | Use |
| --- | --- |
| `connection_status` | Confirm the workspace, user and membership role. |
| `funnel_events` | Received event names, source, correlation keys, property types, client versions. |
| `list_funnels` | Saved funnels with dashboard status, message and URL. |
| `create_funnel` | Create one immutable funnel and publish its dashboard. |
| `list_installation_events` | Current pre-login event names for this installation. |
| `set_installation_events` | Replace the pre-login event list (`{ events: [...] }`). |

## Workflow: configure pre-login events

1. Read the game code and find every `TrackFunnel`/`Track*` call made **before** the user is bound.
   Ignore backend events and anything emitted after login.
2. Keep real, actually sent names only. Do not invent plausible ones.
3. Call `set_installation_events` with the complete list (it replaces, it does not append).
4. Call `list_installation_events` and compare.
5. After the game sends events, `funnel_events` shows what really arrived; use it to correct names.

Limits: unique, nonblank, at most 64 characters each, at most 100 entries. An empty list means
every event without a user id is refused.

## Workflow: create a funnel

1. `funnel_events` → pick stages that have been received, with `source: "client"` unless a backend
   stage is intended.
2. Choose `joinKey` from the stage's reported `correlationKeys` (for example `user_id`,
   `session_id`, `installation_id`). Do not guess a key the catalogue does not list.
3. Call `create_funnel`:

```json
{
  "definition": {
    "name": "onboarding",
    "version": "1.0",
    "clientVersions": ["1.4.0"],
    "active": true,
    "stages": [
      { "eventName": "app_started", "source": "client", "joinKey": null },
      { "eventName": "login_presented", "source": "client", "joinKey": "user_id" },
      { "eventName": "level_completed", "source": "client", "joinKey": "user_id" }
    ]
  }
}
```

4. Read the result and `list_funnels`. If the outcome is unknown, reconcile with `list_funnels`
   before retrying — a second create makes a second funnel.
5. Publication success is not measurement success: funnels count distinct `user_id`, so anonymous
   players who never bind do not appear. Verify counts against real events.

## Workflow: verify SDK delivery

- Await `FlushAsync`/`CloseAsync` and inspect the result. Task completion is not delivery proof.
  Check `Status == DeliveryStatus.Complete`, `Pending == 0` and `Rejected == 0`, or inspect the
  rejected count.
- Rejected > 0 means the server refused those event names — fix the installation-scoped list, not
  the retry.
- A lost response does not prove nothing was stored; IDs are stable and duplicates are deduplicated
  server-side.
