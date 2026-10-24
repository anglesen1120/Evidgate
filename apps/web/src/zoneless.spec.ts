// 00 §5.6 W3 / AC2: the workspace is zoneless. Nothing may pull zone.js in — not a
// dependency, not a polyfill in angular.json, not an import in a spec — so the global that
// zone.js patches into the page must be absent from the test environment itself.
describe('zoneless workspace', () => {
  it('loads no zone.js', () => {
    // zone.js patches a `Zone` global that the ES/DOM lib types do not declare, so the only
    // possible check is a declared view of globalThis; read through a named binding.
    const patchedGlobals = globalThis as typeof globalThis & { readonly Zone?: unknown };
    expect(patchedGlobals.Zone).toBeUndefined();
  });
});
