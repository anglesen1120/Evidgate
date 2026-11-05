import { describe, expect, it } from 'vitest';

import {
  CORE_FILE,
  FEATURE_BOUNDARY,
  FEATURE_FILE,
  SHARED_FILE,
  lint,
  ruleIds,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  describe('no-feature-cross-import', () => {
    it('rejects a sibling feature import, relative or aliased', () => {
      const relative = [
        "import { onboarding } from '../onboarding/store';",
        '',
        'export const store = onboarding;',
        '',
      ].join('\n');
      expect(ruleIds(relative, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);

      const aliased = [
        "import { onboarding } from '@app/features/onboarding/store';",
        '',
        'export const store = onboarding;',
        '',
      ].join('\n');
      expect(ruleIds(aliased, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);

      const barrel = [
        "import { onboardingStore } from '../onboarding';",
        '',
        'export const store = onboardingStore;',
        '',
      ].join('\n');
      expect(ruleIds(barrel, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);
    });

    it('rejects a dynamic import of a sibling feature', () => {
      const code = [
        "export const load = (): Promise<unknown> => import('../onboarding/store');",
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);
    });

    it('allows core and its own feature area', () => {
      const code = [
        "import { ApiGateway } from '../../core/http/api-gateway';",
        "import { store } from './store';",
        '',
        'export const both = [ApiGateway, store];',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });

    it('rejects a core or shared dependency on a feature', () => {
      const code = [
        "import { runs } from '../features/runs/x';",
        '',
        'export const routes = runs;',
        '',
      ].join('\n');
      expect(ruleIds(code, CORE_FILE)).toEqual([FEATURE_BOUNDARY]);
      expect(ruleIds(code, SHARED_FILE)).toEqual([FEATURE_BOUNDARY]);
    });
  });
});
