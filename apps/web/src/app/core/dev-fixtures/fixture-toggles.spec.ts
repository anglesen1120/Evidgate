// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12): the toggle rules of the dev fixture backend.
//
// `FixtureToggles` seeds itself from `document.location.search` in its constructor, so every URL
// case builds a fresh instance over a stubbed `DOCUMENT`; the `set…` cases only need an empty
// search. Latency has one rule for both entry points (a whole, non-negative number of milliseconds,
// clamped to ten minutes, anything else ignored), and these cases pin it on both of them.

import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';

import { ALL_OPERATIONS, DEFAULT_FIXTURE_VARIANT, FixtureToggles } from './fixture-toggles';

/** A fresh root injector whose `DOCUMENT` carries `search`; `''` is a plain page URL. */
function togglesFor(search: string): FixtureToggles {
  const document = { location: { search } } as unknown as Document;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [{ provide: DOCUMENT, useValue: document }],
  });
  return TestBed.inject(FixtureToggles);
}

describe('fixture toggles (09.1.1-T12.1)', () => {
  describe('fixture.latency from the URL', () => {
    it.each([
      ['0', 0],
      ['600000', 600_000],
    ])('accepts the boundary value %p', (value, expected) => {
      expect(togglesFor(`?fixture.latency=${value}`).latencyFor('listRuns')).toBe(expected);
    });

    it('clamps a well-formed value past the ten-minute cap, as setLatency does', () => {
      const toggles = togglesFor('?fixture.latency=600001');

      expect(toggles.latencyFor('listRuns')).toBe(600_000);
      expect(toggles.latencyFor('anyUnlistedOperation')).toBe(600_000);
    });

    it.each([
      '9007199254740991',
      '9007199254740992',
      '9007199254740993',
      '100000000000000000000',
      '9'.repeat(400),
    ])('clamps the well-formed value %p around and above 2^53 to the cap', (value) => {
      expect(togglesFor(`?fixture.latency=${value}`).latencyFor('listRuns')).toBe(600_000);
    });

    it.each(['abc', '-5', '1e3', '1.5', ''])('ignores the unusable value %p', (value) => {
      expect(
        togglesFor(`?fixture.latency=${encodeURIComponent(value)}`).latencyFor('listRuns'),
      ).toBeNull();
    });
  });

  describe('fixture.variant from the URL', () => {
    it('selects the named operation and leaves every other operation on the default', () => {
      const toggles = togglesFor('?fixture.variant.listRuns=forbidden');

      expect(toggles.variantFor('listRuns')).toBe('forbidden');
      expect(toggles.variantFor('listRoles')).toBe(DEFAULT_FIXTURE_VARIANT);
    });

    it("stores '*' for the literal id without applying it to any operation", () => {
      const toggles = togglesFor('?fixture.variant.*=forbidden');

      expect(toggles.variantFor(ALL_OPERATIONS)).toBe('forbidden');
      expect(toggles.variantFor('listRuns')).toBe(DEFAULT_FIXTURE_VARIANT);
      expect(toggles.variantFor('listRoles')).toBe(DEFAULT_FIXTURE_VARIANT);
    });

    it('ignores an empty operation id', () => {
      const toggles = togglesFor('?fixture.variant.=forbidden');

      expect(toggles.variantFor('')).toBe(DEFAULT_FIXTURE_VARIANT);
    });

    it('ignores an empty variant value', () => {
      const toggles = togglesFor('?fixture.variant.listRuns=');

      expect(toggles.variantFor('listRuns')).toBe(DEFAULT_FIXTURE_VARIANT);
    });

    it('stores prototype-shaped ids as plain data without prototype pollution', () => {
      const toggles = togglesFor(
        '?fixture.variant.__proto__=polluted&fixture.variant.constructor=x',
      );

      expect(toggles.variantFor('__proto__')).toBe('polluted');
      expect(toggles.variantFor('constructor')).toBe('x');
      expect(({} as Record<string, unknown>)['polluted']).toBeUndefined();
      expect(Object.prototype.hasOwnProperty.call(Object.prototype, 'polluted')).toBe(false);
    });
  });

  describe('setLatency', () => {
    it('applies a per-operation override over the global one and clears on null', () => {
      const toggles = togglesFor('');
      toggles.setLatency(ALL_OPERATIONS, 300);
      toggles.setLatency('listRuns', 800);

      expect(toggles.latencyFor('listRuns')).toBe(800);
      expect(toggles.latencyFor('listRoles')).toBe(300);

      toggles.setLatency(ALL_OPERATIONS, null);
      expect(toggles.latencyFor('listRuns')).toBe(800);
      expect(toggles.latencyFor('listRoles')).toBeNull();
    });

    it('clamps a well-formed value past the cap, exactly like the URL parameter', () => {
      const toggles = togglesFor('');
      toggles.setLatency('listRuns', 600_001);

      expect(toggles.latencyFor('listRuns')).toBe(600_000);
    });

    it.each([2 ** 53 - 1, 2 ** 53, 2 ** 53 + 2, 1e20])(
      'clamps the whole value %p around and above 2^53 to the cap',
      (value) => {
        const toggles = togglesFor('');
        toggles.setLatency('listRuns', value);

        expect(toggles.latencyFor('listRuns')).toBe(600_000);
      },
    );

    it.each([Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, Number.NaN, -5, 1.5])(
      'ignores the invalid value %p and keeps the override already set',
      (value) => {
        const toggles = togglesFor('');
        toggles.setLatency('listRuns', 250);
        toggles.setLatency('listRuns', value);

        expect(toggles.latencyFor('listRuns')).toBe(250);
      },
    );

    it.each([Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, Number.NaN, -5, 1.5])(
      'never stores the invalid value %p on a fresh service',
      (value) => {
        const toggles = togglesFor('');
        toggles.setLatency('listRuns', value);

        expect(toggles.latencyFor('listRuns')).toBeNull();
      },
    );
  });

  describe('setVariant', () => {
    it('selects a variant and returns to the default on null', () => {
      const toggles = togglesFor('');
      toggles.setVariant('listRuns', 'forbidden');
      expect(toggles.variantFor('listRuns')).toBe('forbidden');

      toggles.setVariant('listRuns', null);
      expect(toggles.variantFor('listRuns')).toBe(DEFAULT_FIXTURE_VARIANT);
    });
  });
});
