/*
 * Flag-direction pins and `Request` type resolution of the request-cap scan
 * (requestCapScan.testing.ts): each form is reported with the form named, its twin stays clean.
 */
import { describe, expect, it } from 'vitest';

import { requestBodyCaps } from './requestCaps.js';
import {
  pinForms,
  typeResolutionForms,
  unreadInputForms,
} from './requestCapScan.pins.fixtures.testing.js';
import { type Finding } from './requestCapScan.context.testing.js';
import { scanSource } from './requestCapScan.testing.js';

const scan = (text: string): Finding[] =>
  scanSource('fixture.ts', text, new Map(), requestBodyCaps, {}).findings;

describe('request body cap scan flag directions and type resolution', () => {
  it.each([...pinForms, ...typeResolutionForms, ...unreadInputForms])(
    'flags %s with the form named and keeps its twin clean',
    (_name, bad, twin, kind, words) => {
      const found = scan(bad);
      expect(found.map((finding) => finding.kind)).toContain(kind);
      expect(found.map((finding) => finding.detail).join('\n')).toContain(words);
      expect(scan(twin)).toEqual([]);
    },
  );
});
