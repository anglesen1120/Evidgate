# Webhook reference receiver

A runnable reference for integrators receiving Evidgate `evaluation.created` webhooks. The verifier
(`src/verify-webhook.ts`) has no dependencies beyond `node:crypto`; `src/server.ts` is a small
`node:http` wrapper around it. The conformance tests (`receiver conformance`) are the executable
contract.

## Delivery

Evidgate sends `POST <your url>` with `Content-Type: application/json` and these headers:

| Header                | Value                                                       |
| --------------------- | ----------------------------------------------------------- |
| `Evidgate-Event-Id`   | The evaluation id (UUID). Stable across every retry/replay. |
| `Evidgate-Event-Type` | `evaluation.created`                                        |
| `Evidgate-Timestamp`  | Unix seconds when this attempt was signed.                  |
| `Evidgate-Signature`  | `v1=<lowercase hex>`; may later hold several `v1=` values.  |

The signed body carries `evaluation_id`, equal to `Evidgate-Event-Id`. It holds ids, hashes and
reasons only, never artifact content or secrets.

## Signature recipe

```
signature = hex( HMAC-SHA256( key = secret, message = timestamp + "." + rawBody ) )
```

- `secret` is the UTF-8 text shown once at registration (`whsec_...`), used as-is: do not decode it.
- `timestamp` is the `Evidgate-Timestamp` header text; `rawBody` is the exact request bytes. Hash the
  bytes you received, never a re-serialised JSON value.
- A MAC computed over the body alone (without `timestamp + "."`) is invalid.
- The header may list several comma-separated `v1=` values (for example during a secret rotation):
  accept the delivery if **any** matches. Ignore entries with another version prefix.
- Compare in constant time (`crypto.timingSafeEqual`) after checking the length.

## Checks, in order

1. All four headers present; the timestamp is digits only.
2. `|now - timestamp| <= 300` seconds, so exactly 5 minutes is accepted and 5 minutes 1 second is not.
   Old and far-future timestamps are both refused.
3. A `v1` MAC matches. Failure is a `401`; record it and do not process the event.
4. The event type is `evaluation.created` and `Evidgate-Event-Id` equals the signed `evaluation_id`
   (the id header is not itself signed, so this stops an attacker pairing a captured body with an id).
5. Deduplicate on `Evidgate-Event-Id`: a second delivery with an id you already processed is acknowledged
   with `2xx` but not processed again. Only authenticated ids are recorded.

`seenEventIds` in the sample is an in-memory `Set`. In production keep it in durable storage shared by
all instances, and record the id in the same transaction as your processing.

## Retry semantics

- Respond with any `2xx` within **10 seconds**. The response body is ignored (at most 4 KiB is read).
- A `5xx`, a non-`2xx` status or a timeout is retried with exponential backoff for **24 hours**; after
  that the delivery is dead-lettered and an operator can replay it by `event_id`. A replay is a new
  attempt of the same event, so it carries the same `Evidgate-Event-Id` and a fresh timestamp and signature.
- **Redirects are not followed.** A `3xx` counts as a failed attempt.
- Delivery is at-least-once: expect duplicates and dedupe as above.
- Endpoints must be public `https` URLs; private, loopback and link-local addresses are refused.

## Run

```
npm test -w @evidgate/webhook-receiver
```

```ts
import { createReceiver } from './src/server.js';

createReceiver({ secret: process.env.WEBHOOK_SECRET ?? '' }).listen(8080);
```
