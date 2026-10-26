// 09.1.1-T5.1 (plan 09.1.1 §10 T5; 09.2.1-T1 hand-over, S71, #159; T38): the ONE validator every
// `returnUrl` in this app goes through — the guard's `/login?returnUrl=…`, the login page's
// `/auth/callback?returnUrl=…` and the callback page's own target.
//
// It lives in `core/routing`, not in the auth feature, because `core` needs it too: the session
// store validates the path it hands to `/login?reason=expired`, and `evidgate/no-feature-cross-import`
// forbids core from reaching into `features/**`. A feature may import core, never the reverse, so the
// shared rule sits below both and there is exactly one implementation.
//
// What a valid value is: a same-origin absolute PATH this app can navigate to —
//   `/orgs/<id>/projects/<id>/overview`, `/runs?status=failed#top`
// and nothing else. The answer is always a string a router navigation accepts, so the caller never
// has to guard a `null`: an unusable value becomes `/` (the workspace root, which the landing guard
// resolves from the server's own lists).
//
// Refused, in the order the checks run:
//   - a value that is not a string, is empty, or is longer than {@link MAX_RETURN_URL_LENGTH};
//   - anything that does not start with exactly one `/`: `foo`, `//evil.example`,
//     `https://evil.example`, `javascript:…`, `data:…` — a scheme can only appear before the first
//     `/`, so requiring the very first character to be `/` (and the second not to be `/`) removes
//     the whole class;
//   - a backslash anywhere (`/\evil.example` is a protocol-relative URL to several browsers) and any
//     C0/C1 control character (a CR/LF or NUL can terminate or split a header or a URL);
//   - an ENCODED form of any of the above: the value is percent-decoded up to
//     {@link MAX_DECODE_PASSES} times and every pass is re-checked, so `/%2F%2Fevil.example`,
//     `/%5Cevil.example` and their double- and triple-encoded spellings are refused, malformed
//     encoding (`/%zz`) is refused, and a value still carrying an encoded layer after four passes is
//     refused rather than trusted;
//   - a path that is itself part of the sign-in flow (`/login`, `/auth/callback`, in any
//     query/fragment spelling and in any encoding), because returning there after a successful
//     sign-in would loop.
//
// The RAW value is what comes back, not the decoded one: decoding is only an inspection step, and
// returning the decoded spelling would let `%3F`/`%23` change the meaning of the path (a `?` inside
// a segment would become a query string).
//
// Pure and dependency-free on purpose: the session store (`core/session`) and the guards
// (`core/guards`) import it, so it must not drag the auth feature's components into their chunk.

/** Longest `returnUrl` this app accepts; the API refuses a longer one on `start` as well. */
export const MAX_RETURN_URL_LENGTH = 512;

/** How many percent-decoding layers are inspected before an encoded value is refused. */
export const MAX_DECODE_PASSES = 4;

/** What every unusable value becomes. */
export const HOME_PATH = '/';

/** Paths that would put the caller back into the sign-in flow; the callback included (a loop). */
const LOOPING_PATHS: readonly string[] = ['/login', '/auth/callback'];

/**
 * C0 controls (includes CR, LF, TAB, NUL), DEL, the C1 block (U+0080-U+009F, where U+0085 is a line
 * break) and the two JavaScript line separators: a value carrying one is never navigated to.
 */
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/;

/** One backslash is enough to become an authority in a browser that normalises it to `/`. */
const BACKSLASH = /\\/;

/** `true` for a value no navigation may receive, whatever decoding layer produced it. */
function hasUnsafeShape(value: string): boolean {
  if (value.length > MAX_RETURN_URL_LENGTH) {
    return true;
  }
  if (!value.startsWith('/') || value.startsWith('//')) {
    return true;
  }
  return BACKSLASH.test(value) || CONTROL_CHARACTERS.test(value);
}

/** One percent-decoding pass, or `null` when the encoding is malformed. */
function decodeOnce(value: string): string | null {
  try {
    return decodeURIComponent(value);
  } catch {
    return null;
  }
}

/**
 * The path with every segment's matrix parameters (`;a=b`) removed — Angular strips them before it
 * matches a route, so `/login;x=1` activates the sign-in page exactly like `/login`, and comparing
 * the raw text would miss that spelling of the loop.
 */
function withoutMatrixParams(path: string): string {
  return path
    .split('/')
    .map((segment) => segment.split(';')[0] ?? '')
    .join('/');
}

/** `true` when this spelling (matrix parameters included) names a sign-in route. */
function isSignInPath(value: string): boolean {
  return LOOPING_PATHS.includes(pathOf(withoutMatrixParams(value)));
}

/** The path part of a value (no query, no fragment), with trailing slashes dropped. */
function pathOf(value: string): string {
  const end = value.search(/[?#]/);
  const path = end === -1 ? value : value.slice(0, end);
  const trimmed = path.replace(/\/+$/, '');
  return trimmed === '' ? HOME_PATH : trimmed;
}

/**
 * The validated `returnUrl` for a query parameter, or {@link HOME_PATH}.
 *
 * Every branch that answers `HOME_PATH` is a value this app must not navigate to: the caller can
 * therefore use the result directly, and a tampered parameter is a redirect home rather than an
 * open redirect or a crash.
 */
export function validateReturnUrl(raw: string | null | undefined): string {
  if (typeof raw !== 'string' || raw === '') {
    return HOME_PATH;
  }
  if (raw.length > MAX_RETURN_URL_LENGTH) {
    return HOME_PATH;
  }
  if (hasUnsafeShape(raw)) {
    return HOME_PATH;
  }

  // Inspect every encoding layer the value could carry. `candidate` is the decoded spelling of the
  // raw value; `raw` is what gets returned.
  let candidate = raw;
  for (let pass = 0; pass < MAX_DECODE_PASSES; pass += 1) {
    const decoded = decodeOnce(candidate);
    if (decoded === null) {
      return HOME_PATH;
    }
    if (decoded === candidate) {
      break;
    }
    if (hasUnsafeShape(decoded)) {
      return HOME_PATH;
    }
    candidate = decoded;
  }
  // A fifth layer means the value is still hiding something: refuse rather than trust it.
  const oneMore = decodeOnce(candidate);
  if (oneMore === null || oneMore !== candidate) {
    return HOME_PATH;
  }

  // A sign-in page as the post-sign-in target is a loop, in any encoding, with any query, and with
  // or without matrix parameters (`/login;a=b` is the same route to Angular).
  if (isSignInPath(candidate) || isSignInPath(raw)) {
    return HOME_PATH;
  }
  return raw;
}
