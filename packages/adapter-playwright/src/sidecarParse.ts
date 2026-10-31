// Parsing one sidecar as plain data: the core YAML schema with no merge keys and no custom tags,
// a walk that refuses anchors/aliases/tags/non-scalar keys and caps depth, nodes and scalars, then
// the sidecar.v1 schema and the per-entry path and id rules. Anything refused answers a warning and
// no entry, never an exception.
import { dirname, join } from 'node:path';

import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { isAlias, isMap, isScalar, isSeq, parseDocument } from 'yaml';
import type { Node as YamlNode, ParseOptions, SchemaOptions } from 'yaml';

import {
  MAX_DEPTH,
  MAX_NODES,
  MAX_SCALAR_CHARS,
  UNCLEAN,
  brief,
  isCanonicalCaseId,
  isRepoPath,
  normalizePath,
} from './sidecarRules.js';
import { SIDECAR_SCHEMA_V1 } from './sidecarSchema.js';
import type { ResolvedEntry, SidecarDocument, Warn } from './sidecarTypes.js';

// Plain data only: the core schema, no merge keys, unique keys, scalar keys, and no custom tags.
// Anchors, aliases and explicit tags are refused by `inspectNode` (the server refuses them too).
const PARSE_OPTIONS: ParseOptions & SchemaOptions = {
  schema: 'core',
  merge: false,
  uniqueKeys: true,
  stringKeys: true,
  strict: true,
  customTags: [],
};

let compiled: ValidateFunction<SidecarDocument> | undefined;

/** The sidecar.v1 validator, compiled once per process. */
function schemaValidator(): ValidateFunction<SidecarDocument> {
  if (compiled === undefined) {
    const ajv = new Ajv2020({ allErrors: false, strict: false });
    ajvFormats.default(ajv);
    compiled = ajv.compile<SidecarDocument>(SIDECAR_SCHEMA_V1);
  }
  return compiled;
}

/** Parses and validates one sidecar; a document that is not valid contributes no entry. */
export function parseEntries(manifestPath: string, content: string, warn: Warn): ResolvedEntry[] {
  const document = parseDocument(content, PARSE_OPTIONS);
  const failure = document.errors[0];
  if (failure !== undefined) {
    const position = failure.linePos?.[0];
    const where =
      position === undefined
        ? failure.code
        : `${failure.code} at line ${position.line}, column ${position.col}`;
    warn('invalid_sidecar', manifestPath, `the sidecar is not plain YAML data (${where})`);
    return [];
  }

  try {
    inspectNode(document.contents, 0, { nodes: 0 });
  } catch (error) {
    const reason = error instanceof Error ? error.message : 'unreadable';
    warn('invalid_sidecar', manifestPath, `the sidecar is not plain YAML data (${reason})`);
    return [];
  }

  let data: unknown;
  try {
    data = JSON.parse(JSON.stringify(document.toJS({ maxAliasCount: 0 })) ?? 'null');
  } catch {
    warn(
      'invalid_sidecar',
      manifestPath,
      'the sidecar is not plain YAML data (the document could not be read as data)',
    );
    return [];
  }

  const validate = schemaValidator();
  if (!validate(data)) {
    const first = validate.errors?.[0];
    const path = first?.instancePath ?? '';
    const keyword = first?.keyword ?? 'invalid';
    warn(
      'invalid_sidecar',
      manifestPath,
      `the sidecar breaks sidecar.v1 at '${path}' (${keyword})`,
    );
    return [];
  }

  return collectEntries(manifestPath, data, warn);
}

/** Turns the validated document into usable entries, warning per entry that names an unusable path. */
function collectEntries(
  manifestPath: string,
  document: SidecarDocument,
  warn: Warn,
): ResolvedEntry[] {
  const directory = dirname(manifestPath);
  const entries: ResolvedEntry[] = [];

  for (const item of document.cases) {
    if (!isRepoPath(item.file)) {
      warn(
        'invalid_sidecar_entry',
        manifestPath,
        `entry '${brief(item.file)}' is not a path inside the manifest's directory`,
      );
      continue;
    }

    const path = normalizePath(join(directory, item.file));

    // The schema's `format: uuid` accepts the `urn:uuid:` prefix and the nil UUID (as the server's
    // format assertion does); the server then refuses both per entry, so the reporter does too.
    if (!isCanonicalCaseId(item['evidgate.id'])) {
      warn('invalid_sidecar_entry', path, 'the evidgate.id of this entry is not a UUID');
      continue;
    }

    if (item.name !== undefined && UNCLEAN.test(item.name)) {
      warn(
        'invalid_sidecar_entry',
        path,
        'the name of this entry has control, format or malformed Unicode characters',
      );
      continue;
    }

    entries.push({ path, id: item['evidgate.id'].toLowerCase() });
  }

  return entries;
}

/** Refuses anchors, aliases, tags, merge keys, non-scalar keys, and caps depth, nodes and scalars. */
function inspectNode(node: YamlNode | null, depth: number, state: { nodes: number }): void {
  if (node === null) {
    return;
  }
  if (depth > MAX_DEPTH || ++state.nodes > MAX_NODES) {
    throw new SidecarFormatError('the document is too deeply nested or too large');
  }
  if (isAlias(node)) {
    throw new SidecarFormatError('aliases are not allowed');
  }
  if (node.anchor !== undefined) {
    throw new SidecarFormatError('anchors are not allowed');
  }
  if (node.tag !== undefined) {
    throw new SidecarFormatError('tags are not allowed');
  }

  if (isScalar(node)) {
    if (typeof node.value === 'string' && node.value.length > MAX_SCALAR_CHARS) {
      throw new SidecarFormatError('a value is longer than 4096 characters');
    }
    return;
  }

  if (isSeq(node)) {
    for (const item of node.items) {
      inspectNode(item as YamlNode | null, depth + 1, state);
    }
    return;
  }

  if (isMap(node)) {
    for (const pair of node.items) {
      if (!isScalar(pair.key)) {
        throw new SidecarFormatError('mapping keys must be plain text');
      }
      inspectNode(pair.key, depth + 1, state);
      if (pair.key.value === '<<') {
        throw new SidecarFormatError('merge keys are not allowed');
      }
      inspectNode(pair.value as YamlNode | null, depth + 1, state);
    }
    return;
  }

  throw new SidecarFormatError('unsupported YAML construct');
}

/** A refusal of the whole document, raised while walking the parsed YAML. */
class SidecarFormatError extends Error {}
