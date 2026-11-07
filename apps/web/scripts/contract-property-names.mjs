// The contract property names the fixture-leak gate (check-no-fixture-code.mjs) treats as
// vocabulary under an error envelope's `details[].path`. Read from `contracts/openapi.yaml` at
// scan time; every failure is reported through the caller's `fail`, so the gate fails closed.
import { existsSync, readFileSync } from 'node:fs';
import { parse as parseYaml } from 'yaml';

/** Schema keywords whose value is sample data, not schema: a `properties` key there is no name. */
const SAMPLE_KEYWORDS = new Set(['example', 'examples', 'default', 'enum', 'const']);

/**
 * Adds the key of every `properties` map found below `node` to `names`. `isNameMap` marks a
 * `properties` map itself, whose keys are property names (even `default`), not keywords.
 */
function collectPropertyNames(node, names, isNameMap = false) {
  if (Array.isArray(node)) {
    for (const item of node) {
      collectPropertyNames(item, names);
    }
    return;
  }
  if (node === null || typeof node !== 'object') {
    return;
  }
  for (const [key, child] of Object.entries(node)) {
    if (!isNameMap && SAMPLE_KEYWORDS.has(key)) {
      continue;
    }
    if (key === 'properties' && !isNameMap) {
      // Only a map is a name list; an array or scalar there is not schema, so it is not walked.
      if (child !== null && typeof child === 'object' && !Array.isArray(child)) {
        for (const name of Object.keys(child)) {
          names.add(name);
        }
        collectPropertyNames(child, names, true);
      }
      continue;
    }
    collectPropertyNames(child, names, false);
  }
}

/** Every property name declared in `components.schemas` of the contract; fails closed. */
export function contractPropertyNames(file, fail, shown) {
  if (!existsSync(file)) {
    fail(`no ${shown(file)} — the contract the error-path exemption reads is missing.`);
  }
  let schemas;
  try {
    schemas = parseYaml(readFileSync(file, 'utf8'))?.components?.schemas;
  } catch (error) {
    fail(`cannot parse ${shown(file)}: ${error instanceof Error ? error.message : String(error)}`);
  }
  const names = new Set();
  collectPropertyNames(schemas, names);
  if (names.size === 0) {
    fail(`${shown(file)} declares no components.schemas property, so the exemption has no names.`);
  }
  return names;
}

/**
 * Whether `value` is a contract property name or a dotted / `/`-pointer path of them, where a
 * non-leading segment may also be an array index (`a.0.b`, `a[0].b`, `/components/0/name`).
 */
export function isContractPath(value, propertyNames) {
  const segments = (value.startsWith('/') ? value.slice(1) : value).split(/[./]/);
  if (segments.some((segment) => segment === '')) {
    return false;
  }
  return segments.every((segment, position) => {
    const bare = segment.replace(/(\[\d+\])+$/, '');
    if (position > 0 && /^\d+$/.test(segment)) {
      return true;
    }
    return propertyNames.has(bare);
  });
}
