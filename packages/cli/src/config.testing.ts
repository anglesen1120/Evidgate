// The baseline environment every config resolver case varies. Each case passes its env
// explicitly, so nothing here reads the process environment; the `CliError` a refusal throws is
// unwrapped by `captureCliError` in `commands/cliSpec.testing`.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
export const baseEnv = {
  EVIDGATE_API_URL: 'https://api.evidgate.test',
  EVIDGATE_PROJECT: 'proj-1',
};
