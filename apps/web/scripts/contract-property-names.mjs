// The contract property names the fixture-leak gate (check-no-fixture-code.mjs) treats as
// vocabulary under an error envelope's `details[].path`. Read from `contracts/openapi.yaml` at
// scan time; every failure is reported through the caller's `fail`, so the gate fails closed.
import { existsSync, readFileSync } from 'node:fs';
import { parse as parseYaml } from 'yaml';

/** Adds the key of every `properties` map found anywhere below `node` to `names`. */
function collectPropertyNames(node, names) {
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
    if (key === 'properties' && child !== null && typeof child === 'object') {
      for (const name of Object.keys(child)) {
        names.add(name);
      }
    }
    collectPropertyNames(child, names);
  }
}

/** Every property name declared in `components.schemas` of the contract; fails closed. */
export function contractPropertyNames(file, fail, shown) {
  if (!existsSync(file)) {
    fail(`no ${file} — the contract the error-path exemption reads is missing.`);
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
