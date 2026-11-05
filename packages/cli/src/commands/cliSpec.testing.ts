// The output, error and `fetch` doubles every command spec shares: a captured `Io`, the
// `CliError` a refusal throws, a recording `fetch` stub with one recorded-request shape, the
// dev-token exchange it can answer in front of the command's own requests, and the JSON answer
// builder. A command spec imports these through its own fixture module (or `gate.kit.testing`)
// instead of carrying a copy.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { gunzipSync } from 'node:zlib';

import { CliError } from '../errors.js';

import type { Io } from '../redact.js';

/** The streams a command wrote to, plus the arrays behind them. */
export interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

export function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

/** The `CliError` a refusal threw; a call that returns normally is a spec bug. */
export function captureCliError(run: () => unknown): CliError {
  try {
    run();
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the call to throw a CliError');
}

/** `captureCliError` for an async refusal: the call is awaited inside the guard. */
export async function captureCliErrorAsync(run: () => Promise<unknown>): Promise<CliError> {
  try {
    await run();
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the call to throw a CliError');
}

/** One recorded request: the request itself, what it carried and what a spec asserts on. */
export interface StubCall {
  readonly request: Request;
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  /** The bytes the command put on the wire (gzipped when it compressed). */
  readonly wire: Buffer;
  /** The decoded body bytes (equal to `wire` unless it was compressed). */
  readonly body: Buffer;
  /** The decoded JSON body; `undefined` when the call carried none (a `PUT` keeps `body`). */
  readonly json: unknown;
  readonly key: string | null;
  /** The `events[]` of a batch body, in request order; empty for every other body. */
  readonly events: ReadonlyArray<Record<string, unknown>>;
}

/** The answers a spec scripts; `index` counts the calls recorded in `calls`, from zero. */
export type StubHandler = (call: StubCall, index: number) => Response | Promise<Response>;

export interface StubOptions {
  /** Answers the dev-token exchange in front of `handler`; such a call lands in `exchanges`. */
  readonly exchange?: (call: StubCall) => Response | undefined;
}

export interface StubHarness {
  readonly fetchImpl: typeof fetch;
  /** The command's own requests; a call the stub answered itself is never one of them. */
  readonly calls: StubCall[];
  /** The calls the stub answered itself (the dev-token exchange), in order. */
  readonly exchanges: StubCall[];
}

/**
 * A `fetch` stub that records every request and answers from `handler` (never the network).
 * A `PUT` body is read directly instead of cloned: a `clone()` on a half-duplex body would tee
 * it and stall until the other branch is drained. A `Content-Encoding: gzip` body is recorded
 * decoded in `body`, with the bytes on the wire kept in `wire`.
 */
export function stubFetch(handler: StubHandler, options: StubOptions = {}): StubHarness {
  const calls: StubCall[] = [];
  const exchanges: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    const wire =
      method === 'PUT' && request.body !== null
        ? Buffer.from(await new Response(request.body).arrayBuffer())
        : Buffer.from(await request.clone().arrayBuffer());
    const body = request.headers.get('content-encoding') === 'gzip' ? gunzipSync(wire) : wire;
    const json = method === 'PUT' || body.length === 0 ? undefined : parseJson(body);
    const call: StubCall = {
      request,
      method,
      url: request.url,
      headers: new Headers(request.headers),
      wire,
      body,
      json,
      key: request.headers.get('idempotency-key'),
      events: eventsOf(json),
    };
    const answer = options.exchange?.(call);
    if (answer !== undefined) {
      exchanges.push(call);
      return answer;
    }
    calls.push(call);
    return handler(call, calls.length - 1);
  };
  return { fetchImpl, calls, exchanges };
}

/** One recorded request and its body as text: the shape an auth spec answers from. */
export interface RequestCall {
  readonly request: Request;
  readonly body: string;
}

/**
 * `stubFetch` for a spec that answers from the `Request` and asserts on its text body: the
 * recording is the same one, projected to the request and the decoded bytes as UTF-8.
 */
export function stubRequestFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: RequestCall[];
} {
  const calls: RequestCall[] = [];
  const { fetchImpl } = stubFetch((call) => {
    calls.push({ request: call.request, body: call.body.toString('utf8') });
    return handler(call.request);
  });
  return { fetchImpl, calls };
}

export function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The decoded JSON body, or `undefined` when the bytes are not JSON. */
function parseJson(body: Buffer): unknown {
  try {
    return JSON.parse(body.toString('utf8')) as unknown;
  } catch {
    return undefined;
  }
}

/** The `events[]` of a parsed batch body, or `[]` for any other body (including `null`). */
function eventsOf(json: unknown): ReadonlyArray<Record<string, unknown>> {
  if (typeof json !== 'object' || json === null) {
    return [];
  }
  const events = 'events' in json ? json.events : undefined;
  if (!Array.isArray(events)) {
    return [];
  }
  return events.filter(
    (event): event is Record<string, unknown> =>
      typeof event === 'object' && event !== null && !Array.isArray(event),
  );
}
