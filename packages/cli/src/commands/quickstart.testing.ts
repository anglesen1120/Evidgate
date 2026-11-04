// Fixtures the `quickstart` specs share: the version read from the manifest (the template
// must pin the shipped one, not a copy), the sample project the committed docs examples use,
// the minimal environment the command needs, and `capture()` from `cliSpec.testing.ts`.
import { readFileSync } from 'node:fs';

/** The exact version the template must pin, read from the manifest, not from the code. */
const packageJson = JSON.parse(
  readFileSync(new URL('../../package.json', import.meta.url), 'utf8'),
) as { version: string };
export const version = packageJson.version;

/** The sample project the committed `docs/quickstart/` examples use. */
export const sampleProject = 'my-project';

/** The minimal environment `quickstart` needs: an API URL and a project, no token. */
export function quickstartEnv(project: string): Record<string, string> {
  return { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: project };
}
