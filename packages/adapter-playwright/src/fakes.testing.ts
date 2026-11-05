// Test fakes implement only the surface the code under test reads, so the rest of a Playwright
// reporter type is missing by design. One assertion over the empty target keeps that partial
// shape typed at the seam instead of at every call site.
export function fake<T extends object>(partial: object): T {
  return Object.assign({} as T, partial);
}
