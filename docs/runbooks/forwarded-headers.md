# Forwarded headers and the client address

The Api keys its rate limits (login, account link, invite, CI token exchange) and writes the `ip` of session rows (`session.ip`) from one value: the client's address. Behind a reverse proxy or load balancer the socket address is the proxy's, so the Api must be told which senders may speak for a client.

## Configuration

| Key | Meaning |
|---|---|
| `EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES` | Comma-separated proxy IP addresses, or `none` when no proxy sits in front of the Api |
| `EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS` | Comma-separated proxy CIDR networks, e.g. `10.0.0.0/8` |

- Outside Development the Api **refuses to start** unless a proxy or network is listed or `KNOWNPROXIES=none` is set. `none` together with `KNOWNNETWORKS` is refused, and so is a value that does not parse (the start-up message names the key and the entry).
- `X-Forwarded-For` and `X-Forwarded-Proto` are honoured; the host is never forwarded (see the end of this list).
- A request from a sender that is not listed has its `X-Forwarded-For` ignored, so a client cannot choose its own bucket.
- With a chain of proxies, list every one of them: the middleware reads the header from the right and stops at the first hop that is not listed; that hop is the client. A proxy missing from the list makes that proxy the client for every request.
- Loopback is not trusted unless listed.
- Every listed proxy must overwrite or append the peer address in `X-Forwarded-For`; a proxy that passes the header through unchanged lets a client choose its own address and bucket.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` makes the framework trust any sender. Outside Development the Api refuses to start with it set; in Development it is tolerated and keeps the framework's behaviour when no proxy is listed (the review stack relies on it), so spoofing resistance holds only where a proxy is listed.
- `X-Forwarded-For` and `X-Forwarded-Proto` are processed, from the listed proxies and networks only; `X-Forwarded-Host` is never processed. A proxy that terminates TLS must send `X-Forwarded-Proto: https` (and overwrite any value the client sent), which makes `Request.IsHttps` true for the Secure-only routes (the Development-only dev-session route reads it). Header symmetry is off: a proxy that sets only one of the two headers is accepted. `none` or an unlisted sender: both headers are ignored.
- The client address is stored in `session.ip` and keys the limiters; the identity audit events carry no address today.

## Check

1. Set the keys, restart, and send two requests through the proxy with different real client addresses to a limited route (for example the CI token exchange). The two clients must have separate buckets.
2. Send a request straight to the Api (not through the proxy) with a forged `X-Forwarded-For`: the limit must apply to your own address.
3. Remove both keys in Production: the Api must exit at start with the message naming `EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES`.
