// 01.2.1-T1: ESLint 10 flat config (CommonJS — the root package.json has no
// "type":"module" because spectral custom functions under scripts/spectral are
// CommonJS and loaded via require).
// T47: server-side (C#) boundaries are enforced by tests/Architecture.Tests, not
// ESLint — this config covers apps/web and packages/* TypeScript only (00 §5.6).
const tseslint = require('typescript-eslint');

module.exports = tseslint.config(
  {
    ignores: [
      '**/node_modules/**',
      '**/dist/**',
      '**/bin/**',
      '**/obj/**',
      '**/coverage/**',
      '**/*.tsbuildinfo',
      '.husky/_/**',
    ],
  },
  {
    files: ['apps/web/**/*.{ts,tsx}', 'packages/**/*.{ts,tsx}'],
    // 01.6.1-T1.2: specs and Vitest configs are excluded from the emitting build
    // projects (`exclude` in every workspace tsconfig.json), so the project service
    // cannot open them. The block below types them against tsconfig.spec.json instead.
    ignores: ['**/*.test.ts', '**/vitest.config.ts'],
    extends: [tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: __dirname,
      },
    },
    rules: {
      // AC3: floating promises and `any` are hard errors (npm run lint also uses
      // --max-warnings 0, so a warning is already a failure; these are pinned as
      // errors so the severity is explicit).
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
    },
  },
  {
    // 01.6.1-T1.2: `*.test.ts` (Vitest), the per-workspace `vitest.config.ts` and the two
    // root Vitest configs are type-checked against a no-emit tsconfig.spec.json. A project
    // that is only referenced from tsconfig.json is not visible to the project service,
    // hence the explicit list — keep it in sync with the root tsconfig.spec.json and the
    // workspaces of the root package.json.
    files: ['vitest.config.ts', 'vitest.config.base.mts', '**/*.test.ts', '**/vitest.config.ts'],
    extends: [tseslint.configs.recommendedTypeChecked],
    languageOptions: {
      parserOptions: {
        project: [
          './tsconfig.spec.json',
          './apps/web/tsconfig.spec.json',
          './packages/*/tsconfig.spec.json',
        ],
        tsconfigRootDir: __dirname,
      },
    },
    rules: {
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
    },
  },
);
