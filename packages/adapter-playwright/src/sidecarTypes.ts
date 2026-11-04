// The shapes the sidecar loader exchanges: the warning, the index it answers, the filesystem seam
// and the parsed-document types. Types only — the reader is `sidecarRead`, the parser
// `sidecarParse` and the loader `sidecarLoad`.

/** One problem found while reading a sidecar. `path` is the manifest, or the described file. */
export interface SidecarWarning {
  code: 'invalid_sidecar' | 'invalid_sidecar_entry' | 'sidecar_conflict';
  path: string;
  message: string;
}

/** The `location → case_id` index built from every sidecar of a run. */
export interface SidecarIndex {
  /** The case id (lower-case UUID) for an absolute test file path, undefined when unmapped or held by a conflict. */
  lookup(testFile: string): string | undefined;
  readonly warnings: readonly SidecarWarning[];
  /** Warnings the loader discarded past its own cap; the reporter adds them to its suppressed tally. */
  readonly droppedWarnings: number;
}

/** The file-system seam: `readFile` returns the sidecar bytes (at most `maxBytes + 1`), or undefined when it is absent. */
export interface SidecarFs {
  readFile(path: string, maxBytes: number): Uint8Array | undefined;
}

/** One usable mapping of a sidecar: the absolute path of the described file and its id. */
export interface ResolvedEntry {
  path: string;
  id: string;
}

/** One `cases[]` item, as the sidecar.v1 schema guarantees it. */
export interface SidecarCase {
  file: string;
  'evidgate.id': string;
  name?: string;
  /** Optional external requirement pointer; the reporter resolves ids only, the server stores and validates it. */
  requirement?: string;
}

/** A sidecar document that passed the sidecar.v1 schema. */
export interface SidecarDocument {
  version: 1;
  cases: SidecarCase[];
}

/** The result of inspecting a manifest path before any content is read. */
export type Inspection =
  { kind: 'absent' } | { kind: 'invalid'; reason: string } | { kind: 'file'; size: number };

export type Warn = (code: SidecarWarning['code'], path: string, message: string) => void;

/**
 * Where the loader may look for a manifest.
 */
export interface SidecarLoadOptions {
  /**
   * The highest directory whose `evidgate.cases.yaml` may be read — the Playwright
   * `FullConfig.rootDir`. Every test file's directory and its ancestors up to (and including)
   * this root are searched; nothing above it is. Pinning the root keeps the answer independent
   * of which test files a *shard* happens to contain: the common ancestor of a shard's files can
   * be deeper than the run's, so without a pin two shards could resolve different `case_id`s for
   * the same test (or one shard could read a manifest the other cannot). Absent → the historical
   * derivation (the common ancestor of the given test files), which is what a call without a
   * Playwright config (tests, tools) gets.
   */
  rootDir?: string;
}
