// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: OTLP endpoint from config, or a
// no-op exporter when unset): where the telemetry chunk learns where (and how much) to export.
//
// The values come from two `<meta>` tags of `index.html`, read once at boot:
//
//   <meta name="evidgate-otlp-endpoint" content="https://otlp.example.com/v1/traces">
//   <meta name="evidgate-telemetry-sample-ratio" content="0.1">
//
// This story does not edit `index.html` (09.1.1-T10 owns the document and the CSP); until the
// hosting edge injects both tags, the endpoint is unset and the app traces locally against a
// no-op exporter. A missing or malformed value is never an error: it falls back to "export
// nothing" (endpoint) or "sample everything" (ratio), because a typo in a deployment tag must
// not take the app's error handling or its traces down with it.
import { DOCUMENT } from '@angular/common';
import { inject, InjectionToken } from '@angular/core';

/** The meta names the hosting edge injects; they are part of the deploy contract, not an API. */
const ENDPOINT_META_NAME = 'evidgate-otlp-endpoint';
const SAMPLE_RATIO_META_NAME = 'evidgate-telemetry-sample-ratio';
const SERVICE_VERSION_META_NAME = 'evidgate-service-version';

/** The shape `service.version` must have to survive `ScrubbingSpanExporter` (`SCOPE_VERSION`). */
const SERVICE_VERSION = /^[0-9A-Za-z.+-]{1,32}$/;

/** The ratio used when the tag is absent or unparsable: export every trace. */
const DEFAULT_SAMPLE_RATIO = 1;

/** What the telemetry chunk needs: where to export, and what fraction of traces to keep. */
export interface TelemetryConfig {
  /** The OTLP/HTTP traces URL, or `null` when export is disabled (no network call is made). */
  readonly endpoint: string | null;
  /** Root sampling ratio in `[0, 1]`; `1` samples every route change. */
  readonly sampleRatio: number;
  /** The build version recorded as `service.version`; absent when the tag is unset or malformed. */
  readonly serviceVersion?: string;
}

/** The trimmed `content` of `name`, or `null` when the tag is absent or empty. */
function readMetaContent(doc: Document, name: string): string | null {
  const element = doc.querySelector(`meta[name="${name}"]`);
  const content = element?.getAttribute('content')?.trim() ?? '';
  return content.length > 0 ? content : null;
}

/**
 * The endpoint the exporter may POST to, or `null`. Accepted: an absolute `https:` URL with no
 * userinfo, no query string and no fragment. Everything else — a relative URL, `http:` on any
 * host (localhost included: the app CSP only ever names an `https:` origin), another scheme,
 * credentials in the authority — is treated as unset, not repaired: exporting to a host the
 * deploy did not name would be worse than exporting nothing.
 *
 * The returned value is `URL.toString()`, so the exporter receives one canonical spelling.
 */
export function parseOtlpEndpoint(raw: string | null): string | null {
  if (raw === null) {
    return null;
  }
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:') {
    return null;
  }
  if (url.username !== '' || url.password !== '' || url.search !== '' || url.hash !== '') {
    return null;
  }
  return url.toString();
}

/**
 * The sampling ratio, or `1`. Only a finite number written as a plain decimal in `[0, 1]`
 * counts; `1.5`, `-0.1`, `NaN`, `Infinity` and any other text are all "sample everything"
 * rather than a clamp — a misconfigured ratio must not silently turn tracing off.
 */
export function parseSampleRatio(raw: string | null): number {
  if (raw === null) {
    return DEFAULT_SAMPLE_RATIO;
  }
  const value = Number(raw);
  if (Number.isFinite(value) && value >= 0 && value <= 1) {
    return value;
  }
  return DEFAULT_SAMPLE_RATIO;
}

/**
 * Read the telemetry configuration from `doc` (the application document in the browser).
 * Pure and total: it never throws, and it reads the document only once per call.
 */
export function readTelemetryConfig(doc: Document): TelemetryConfig {
  const serviceVersion = parseServiceVersion(readMetaContent(doc, SERVICE_VERSION_META_NAME));
  return {
    endpoint: parseOtlpEndpoint(readMetaContent(doc, ENDPOINT_META_NAME)),
    sampleRatio: parseSampleRatio(readMetaContent(doc, SAMPLE_RATIO_META_NAME)),
    ...(serviceVersion === null ? {} : { serviceVersion }),
  };
}

/**
 * The build version, or `null`. Only a short token of letters, digits and `.+-` counts; anything
 * else (a path, a URL, free text) is dropped rather than repaired, because the value is exported.
 */
export function parseServiceVersion(raw: string | null): string | null {
  return raw !== null && SERVICE_VERSION.test(raw) ? raw : null;
}

/** The browser privacy signals `isTelemetryOptedOut` reads; every field may be absent. */
export interface PrivacySignals {
  readonly doNotTrack?: string | null;
  readonly msDoNotTrack?: string | null;
  readonly globalPrivacyControl?: boolean;
}

/**
 * True when the visitor asked not to be tracked: Do-Not-Track `"1"` (or the legacy `"yes"` /
 * `msDoNotTrack` spelling) or Global Privacy Control `true`. Either signal is enough, and the
 * answer is read once at start: an opted-out visit loads no telemetry chunk, creates no span,
 * adds no `traceparent` and makes no network call. Nothing is persisted (plan 09.1.1 §8).
 */
export function isTelemetryOptedOut(signals: PrivacySignals | null): boolean {
  if (signals === null) {
    return false;
  }
  const dnt = [signals.doNotTrack, signals.msDoNotTrack];
  return (
    dnt.some((value) => value === '1' || value === 'yes') || signals.globalPrivacyControl === true
  );
}

/**
 * The configuration the facade hands to the telemetry chunk. Reading it eagerly at injection
 * time — not inside the dynamic import — keeps the document read in one place and lets a spec
 * assert exactly what the chunk receives.
 */
export const TELEMETRY_CONFIG = new InjectionToken<TelemetryConfig>('evidgate.telemetry-config', {
  providedIn: 'root',
  factory: (): TelemetryConfig => readTelemetryConfig(inject(DOCUMENT)),
});

/** Whether this visit opted out; read once from the document's window, `false` without one. */
export const TELEMETRY_OPT_OUT = new InjectionToken<boolean>('evidgate.telemetry-opt-out', {
  providedIn: 'root',
  factory: (): boolean => isTelemetryOptedOut(inject(DOCUMENT).defaultView?.navigator ?? null),
});
