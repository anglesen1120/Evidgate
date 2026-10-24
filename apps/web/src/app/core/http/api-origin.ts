// 09.1.1-T3.2 (00 §9.1, ADR-0008 rules 1 and 3): the API shares the page's origin and
// lives under `/v1`. Cross-cutting interceptors use this predicate so a foreign-origin or
// non-API request never receives an API header, and `/internal/*`/`/metrics` stay bare.

/** Path prefix of every API route (00 §9.1); operational endpoints live outside it. */
export const API_BASE_PATH = '/v1';

function isApiPath(pathname: string): boolean {
  return pathname === API_BASE_PATH || pathname.startsWith(`${API_BASE_PATH}/`);
}

/**
 * True only when `url` resolves against `pageHref` to the same origin as the page and its
 * path is `/v1` or `/v1/...`. A foreign origin, a protocol-relative URL, another path
 * (`/v2`, `/v1evil`, `/internal/...`) and anything unparsable are all `false`.
 */
export function isApiRequestUrl(url: string, pageHref: string): boolean {
  let page: URL;
  try {
    page = new URL(pageHref);
  } catch {
    return false;
  }
  let target: URL;
  try {
    target = new URL(url, page);
  } catch {
    return false;
  }
  return target.origin === page.origin && isApiPath(target.pathname);
}
