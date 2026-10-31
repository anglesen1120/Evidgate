// Building the `location → case_id` index of a run: which manifests may be read (each test file's
// directory and its ancestors up to the lookup root), the per-run byte budget, the conflict rule
// and the "the entry names a file that is not in the tree" rule. Reading and parsing are the other
// modules' work; this one decides which manifests are visited and what each entry means.
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { defaultFs, inspectSidecar, readText } from './sidecarRead.js';
import { parseEntries } from './sidecarParse.js';
import {
  MAX_BYTES,
  MAX_TOTAL_BYTES,
  MAX_WARNINGS,
  SIDECAR_FILE_NAME,
  normalizePath,
} from './sidecarRules.js';
import type {
  SidecarFs,
  SidecarIndex,
  SidecarLoadOptions,
  SidecarWarning,
  Warn,
} from './sidecarTypes.js';

/**
 * Reads the `evidgate.cases.yaml` of every test file's directory and of every ancestor directory up
 * to the lookup root — the Playwright `config.rootDir` when the caller has one
 * ({@link SidecarLoadOptions}), else the common ancestor of all test files — and returns the
 * `location → case_id` index. The server reads every manifest of the tree and resolves each entry
 * with `RepoPath.Combine(manifest directory, file)`, so a manifest in an ancestor directory can map
 * a test in a subdirectory. Each manifest is read once; a directory without a sidecar contributes
 * nothing and no warning. A sidecar that is refused contributes no entry at all.
 *
 * A test file outside the pinned root contributes only its own directory: the loader never walks
 * above the root, so a manifest unrelated to the run cannot be read (the per-entry and symlink
 * hardening below is unchanged).
 */
export function loadSidecarIndex(
  testFiles: Iterable<string>,
  fs: SidecarFs = defaultFs,
  options: SidecarLoadOptions = {},
): SidecarIndex {
  const warnings: SidecarWarning[] = [];
  let droppedWarnings = 0;
  const warn: Warn = (code, path, message) => {
    if (warnings.length < MAX_WARNINGS) {
      warnings.push({ code, path, message });
    } else {
      droppedWarnings += 1;
    }
  };

  const directories = new Set<string>();
  for (const file of testFiles) {
    directories.add(dirname(normalizePath(file)));
  }

  // A pinned root makes the manifest set independent of the shard's own test files. Without one,
  // the common ancestor of the given files stands in for the repository root.
  const root =
    options.rootDir !== undefined && options.rootDir.length > 0
      ? normalizePath(options.rootDir)
      : commonAncestor([...directories]);

  const { index, conflicted } = readManifests(collectManifests(directories, root), fs, warn);

  for (const path of conflicted) {
    index.delete(path);
  }

  return {
    lookup: (testFile) => index.get(normalizePath(testFile)),
    warnings,
    droppedWarnings,
  };
}

/** Every manifest path the loader may visit: each directory and its ancestors up to `root`. */
function collectManifests(directories: Iterable<string>, root: string): Set<string> {
  const manifests = new Set<string>();
  for (const directory of directories) {
    let current = directory;
    for (;;) {
      manifests.add(join(current, SIDECAR_FILE_NAME));
      if (current === root) {
        break;
      }
      // Never walk above the pinned root: a test file outside it contributes its own directory
      // only (the walk stops before the first ancestor outside the root).
      if (!isWithinRoot(current, root)) {
        break;
      }
      const parent = dirname(current);
      if (parent === current) {
        break;
      }
      current = parent;
    }
  }
  return manifests;
}

/**
 * Reads every manifest in turn under one aggregate byte budget, keeping the first id seen for a
 * path and reporting a second, different id as a conflict. Returns the index and the paths whose
 * entries disagreed, which the caller removes from the index.
 */
function readManifests(
  manifests: Iterable<string>,
  fs: SidecarFs,
  warn: Warn,
): { index: Map<string, string>; conflicted: Set<string> } {
  const index = new Map<string, string>();
  const conflicted = new Set<string>();
  const exists = new Map<string, boolean>();
  let remaining = MAX_TOTAL_BYTES;
  let budgetReported = false;

  for (const manifestPath of manifests) {
    if (remaining <= 0) {
      if (!budgetReported) {
        budgetReported = true;
        warn(
          'invalid_sidecar',
          manifestPath,
          'the sidecars of this run exceed the 8 MiB budget; the rest were skipped',
        );
      }
      continue;
    }

    const size = inspectManifest(manifestPath, warn);
    if (size === undefined) {
      continue;
    }

    // Charge the budget for every manifest that is read, valid or not: a tree of refused manifests
    // must not be able to spend unbounded parse time.
    remaining -= size;

    const text = readText(manifestPath, fs, warn);
    if (text === undefined) {
      continue;
    }

    addEntries(manifestPath, text, index, conflicted, exists, warn);
  }

  return { index, conflicted };
}

/**
 * The size of a manifest that may be read, or undefined (with its warning) when it is absent, not
 * a regular file, or larger than the per-file cap.
 */
function inspectManifest(manifestPath: string, warn: Warn): number | undefined {
  const inspection = inspectSidecar(manifestPath);
  if (inspection.kind === 'absent') {
    return undefined;
  }
  if (inspection.kind === 'invalid') {
    warn('invalid_sidecar', manifestPath, inspection.reason);
    return undefined;
  }
  if (inspection.size > MAX_BYTES) {
    warn('invalid_sidecar', manifestPath, 'the sidecar is larger than 1 MiB');
    return undefined;
  }
  return inspection.size;
}

/** Adds one manifest's entries: the first id wins, a second different id is a conflict, and an entry must name a file in the tree. */
function addEntries(
  manifestPath: string,
  text: string,
  index: Map<string, string>,
  conflicted: Set<string>,
  exists: Map<string, boolean>,
  warn: Warn,
): void {
  for (const entry of parseEntries(manifestPath, text, warn)) {
    const earlier = index.get(entry.path);
    if (earlier !== undefined) {
      if (earlier !== entry.id) {
        conflicted.add(entry.path);
        warn('sidecar_conflict', entry.path, 'two sidecar entries give this file different ids');
      }
      continue;
    }

    let present = exists.get(entry.path);
    if (present === undefined) {
      present = existsSync(entry.path);
      exists.set(entry.path, present);
    }
    if (!present) {
      warn(
        'invalid_sidecar_entry',
        entry.path,
        `the sidecar ${manifestPath} names a file that is not in the tree`,
      );
      continue;
    }

    index.set(entry.path, entry.id);
  }
}

/** The deepest directory that contains every given absolute, forward-slashed directory. */
function commonAncestor(directories: string[]): string {
  const [first, ...rest] = directories;
  if (first === undefined) {
    return '/';
  }

  let common = first.split('/');
  for (const directory of rest) {
    const parts = directory.split('/');
    let length = 0;
    while (length < common.length && length < parts.length && common[length] === parts[length]) {
      length += 1;
    }
    common = common.slice(0, length);
  }

  return common.join('/') || '/';
}

/**
 * True when `directory` is the root or a directory below it. Both are absolute, forward-slashed
 * forms from {@link normalizePath}, so a plain prefix test on a separator boundary is exact:
 * `/repo2` is not inside `/repo`.
 */
function isWithinRoot(directory: string, root: string): boolean {
  if (directory === root) {
    return true;
  }
  const prefix = root.endsWith('/') ? root : `${root}/`;
  return directory.startsWith(prefix);
}
