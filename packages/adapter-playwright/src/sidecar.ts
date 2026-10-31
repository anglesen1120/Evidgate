// The `evidgate.cases.yaml` sidecar loader.
// The file is untrusted input from a customer repository, so it is read as *data*: the size is
// capped before the content is read, the YAML is parsed with the core schema and refused when it
// carries an anchor, alias, tag, merge key, duplicate key or a second document, the result is
// validated against the embedded sidecar.v1 schema, and every entry is checked against the
// repository path rules so it can never name a file outside the manifest's own directory.
// A manifest is looked up in each test file's own directory and in every ancestor directory up to
// the common ancestor of the test files (the server reads every manifest of the tree and resolves
// each entry with `RepoPath.Combine`), each manifest is read once, and the bytes read for one run
// are capped in aggregate. Warnings are data — the identity layer turns them into `run.log`
// envelopes — and a message never echoes file content beyond the offending path or key.
//
// This module is the public surface: the walk is `sidecarLoad.ts`, the reader `sidecarRead.ts`, the
// parser `sidecarParse.ts`, the shared limits and text rules `sidecarRules.ts` and the shapes
// `sidecarTypes.ts`.
export { brief, isCanonicalCaseId, SIDECAR_FILE_NAME } from './sidecarRules.js';
export { loadSidecarIndex } from './sidecarLoad.js';
export type {
  SidecarFs,
  SidecarIndex,
  SidecarLoadOptions,
  SidecarWarning,
} from './sidecarTypes.js';
