/*
 * Declaration handling and closed limits of the request-cap scan (requestCapScan.testing.ts):
 * every declaration of a name is checked, a shadowed name is not another's, and the forms the
 * scan could not read are now reported with the form named.
 */
import { describe, expect, it } from 'vitest';

import { requestBodyCaps } from './requestCaps.js';
import {
  closedLimitForms,
  everyDeclarationForms,
  shadowedCleanSources,
} from './requestCapScan.limits.fixtures.testing.js';
import { type Finding, scanSource } from './requestCapScan.testing.js';

const fixtureRoutes = new Map([
  ['postWidgets', 'POST /widgets'],
  ['createRun', 'POST /projects/{p}/runs'],
]);
const scan = (text: string): Finding[] =>
  scanSource('fixture.ts', text, fixtureRoutes, requestBodyCaps, {}).findings;

describe('request body cap scan declarations and closed limits', () => {
  it.each([...everyDeclarationForms, ...closedLimitForms])(
    'flags %s with the form named and keeps its twin clean',
    (_name, bad, twin, kind, words) => {
      const found = scan(bad);
      expect(found.map((finding) => finding.kind)).toContain(kind);
      expect(found.map((finding) => finding.detail).join('\n')).toContain(words);
      expect(scan(twin)).toEqual([]);
    },
  );

  it.each(shadowedCleanSources)('does not flag a shadowed name: %s', (_name, source) => {
    expect(scan(source)).toEqual([]);
  });
});
