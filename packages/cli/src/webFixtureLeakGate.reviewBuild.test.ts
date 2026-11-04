// The review web image is built from a `review` configuration, not from `production`: the review
// stack has no human login yet, so it carries the Development-only sign-in form, while the
// production bundle must stay form-free. These assertions read the two definitions instead of
// building either, so a drift between them fails without a docker build.
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const ANGULAR_JSON = fileURLToPath(new URL('../../../apps/web/angular.json', import.meta.url));
const WEB_PACKAGE_JSON = fileURLToPath(new URL('../../../apps/web/package.json', import.meta.url));

interface FileReplacement {
  readonly replace: string;
  readonly with: string;
}

interface BuildConfiguration {
  readonly fileReplacements?: readonly FileReplacement[];
  readonly [key: string]: unknown;
}

interface AngularConfiguration {
  readonly projects: {
    readonly web: {
      readonly architect: {
        readonly build: {
          readonly defaultConfiguration: string;
          readonly configurations: Record<string, BuildConfiguration>;
        };
      };
    };
  };
}

interface WebPackage {
  readonly scripts: Record<string, string>;
}

describe('review build configuration', () => {
  async function configurations(): Promise<Record<string, BuildConfiguration>> {
    const angular = JSON.parse(await readFile(ANGULAR_JSON, 'utf8')) as AngularConfiguration;
    return angular.projects.web.architect.build.configurations;
  }

  it('review_is_production_plus_only_the_sign_in_and_run_views_replacements', async () => {
    const build = await configurations();
    const review = build.review;
    const production = build.production;

    if (!review || !production) {
      throw new Error('angular.json must define the production and review build configurations');
    }
    expect(review.fileReplacements).toEqual([
      {
        replace: 'src/app/features/auth/login/login-extension.ts',
        with: 'src/app/features/auth/login/login-extension.development.ts',
      },
      {
        replace: 'src/app/shell/nav/review-entries.ts',
        with: 'src/app/shell/nav/review-entries.review.ts',
      },
    ]);

    const reviewWithoutReplacements: Record<string, unknown> = { ...review };
    delete reviewWithoutReplacements.fileReplacements;
    expect(reviewWithoutReplacements).toEqual(production);
  });

  it('production_carries_no_file_replacement', async () => {
    const production = (await configurations()).production;

    if (!production) {
      throw new Error('angular.json must define the production build configuration');
    }
    expect(production.fileReplacements).toBeUndefined();
  });

  it('build_review_runs_the_review_configuration_behind_both_gates', async () => {
    const pkg = JSON.parse(await readFile(WEB_PACKAGE_JSON, 'utf8')) as WebPackage;

    expect(pkg.scripts['build:review']).toContain('ng build --configuration review');
    expect(pkg.scripts['build:review']).toContain('node scripts/check-bundle-budget.mjs');
    expect(pkg.scripts['build:review']).toContain(
      'node scripts/check-no-fixture-code.mjs --allow-dev-sign-in --require-run-views',
    );
    expect(pkg.scripts.build).not.toContain('--allow-dev-sign-in');
    expect(pkg.scripts.build).not.toContain('--require-run-views');
  });
});
