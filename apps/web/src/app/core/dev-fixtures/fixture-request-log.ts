// The fixtures build answers the API in process (`HttpBackend`), so no `/v1` request reaches the
// network and `page.on('request')` sees none of them. This log is the seam a real-browser spec reads
// instead: every request the fixture backend handled, with the headers the interceptor chain
// produced. Keeping it in its own module lets the Playwright specs import the name and the type
// without pulling Angular into their process.

/** The page-global array the fixture backend appends to, read by `e2e/shell/tenant-header.spec.ts`. */
export const FIXTURE_REQUEST_LOG = '__evidgateFixtureRequests' as const;

/** One handled request. Header names are lowercased; each value list is what was sent. */
export interface FixtureCallRecord {
  readonly method: string;
  readonly url: string;
  readonly headers: Readonly<Record<string, readonly string[]>>;
}

declare global {
  interface Window {
    __evidgateFixtureRequests?: FixtureCallRecord[];
  }
}
