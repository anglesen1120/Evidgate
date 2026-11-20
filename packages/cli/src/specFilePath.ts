// The `file` a Playwright list report names is the path the adapter records: project-relative
// POSIX. It is normalised here once per distinct string in a parse, and bounded, so a crafted
// report can neither grow a path nor make the walk re-validate one suite file per spec.
import { CliError } from './errors.js';

/** A spec file longer than this cannot be a real path; the report is malformed. */
const MAX_SPEC_FILE_CHARS = 4096;
// A path with a C0/DEL control character cannot name a file a person wrote, and a NUL would
// truncate the path at the syscall boundary, so it is a malformed report, not an escape.
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f]/;

/**
 * Normalises each distinct file string once per parse: one suite file is repeated by every spec
 * inside it, so the walk would otherwise validate the same string for each. A refusal throws out
 * of the parse, so only successful normalisations enter the cache.
 */
export function projectRelativePathMemo(): (raw: string) => string {
  const cache = new Map<string, string>();
  return (raw) => {
    const known = cache.get(raw);
    if (known !== undefined) {
      return known;
    }
    const file = projectRelativePath(raw);
    cache.set(raw, file);
    return file;
  };
}

/** The report's own file string as the adapter's project-relative POSIX form, or a refusal. */
function projectRelativePath(raw: string): string {
  if (raw.length > MAX_SPEC_FILE_CHARS) {
    throw malformed('a spec file path is too long');
  }
  if (CONTROL_CHARACTERS.test(raw)) {
    throw malformed('a spec file path contains a control character');
  }
  if (/^[A-Za-z]:[\\/]/.test(raw)) {
    throw outsideRoot('a spec file is named by an absolute path');
  }
  const file = raw.replaceAll('\\', '/');
  if (file.startsWith('/')) {
    throw outsideRoot('a spec file is named by an absolute path');
  }
  const segments: string[] = [];
  for (const segment of file.split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }
    if (segment === '..') {
      if (segments.length === 0) {
        throw outsideRoot('a spec file resolves outside the project root');
      }
      segments.pop();
      continue;
    }
    segments.push(segment);
  }
  if (segments.length === 0) {
    throw outsideRoot('a spec file resolves to the project root itself');
  }
  return segments.join('/');
}

function malformed(message: string): CliError {
  return new CliError(message, { code: 'manifest_report_invalid' });
}

function outsideRoot(message: string): CliError {
  return new CliError(message, { code: 'manifest_path_outside_root' });
}
