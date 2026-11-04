// The seams every config spec shares: the baseline environment each resolver case varies, and
// the unwrapper for the `CliError` a refusal throws. Every case passes its env explicitly, so
// nothing here reads the process environment.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { CliError } from './errors.js';

export const baseEnv = {
  EVIDGATE_API_URL: 'https://api.evidgate.test',
  EVIDGATE_PROJECT: 'proj-1',
};

export function captureCliError(run: () => unknown): CliError {
  try {
    run();
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the call to throw a CliError');
}
