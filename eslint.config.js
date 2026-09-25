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
);
