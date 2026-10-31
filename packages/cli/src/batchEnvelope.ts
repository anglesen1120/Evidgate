// The per-line envelope verdict: the same Ajv registry, error mapping and `details[]` rows
// the server computes, so a line that would be a 422 never leaves the machine. The packaged
// schemas keep it free of any contracts-directory or working-directory dependency.
import { loadPackagedContractSchemas } from './packagedSchemas.js';
import { invalidJsonReason, loadContractSchemas, type ContractSchemaSet } from './validate.js';

import { envelopeSchemaKey, eventsMember, wrappedItemPrefix } from './batchLimits.js';
import { EnvelopeInvalidError, eventIdOf } from './batchErrors.js';

import type { EnvelopeDetail } from './batchErrors.js';

/**
 * Builds the pre-send validator for one spool line. Each line is wrapped as a one-item batch
 * on purpose: validated bare, a batch-shaped line would be accepted as a body of its own.
 */
export function createEnvelopeValidator(
  contractsDirectory?: string,
): (line: string, sequence: number) => void {
  const schemas =
    contractsDirectory === undefined
      ? loadPackagedContractSchemas()
      : loadContractSchemas(contractsDirectory);
  return (line, sequence) => {
    const details = validateEnvelopeLine(schemas, line);
    if (details.length === 0) {
      return;
    }
    const eventId = eventIdOf(line);
    throw new EnvelopeInvalidError({
      sequence,
      ...(eventId === undefined ? {} : { eventId }),
      details,
    });
  };
}

/**
 * The mapped `details[]` rows of one line, with the wrapped-batch prefix stripped.
 *
 * The line is validated as a one-item batch (`{"events":[<line>]}`) through the
 * registry's parsed-document path rather than its batch/no-batch dispatch,
 * because a single-event object takes the item branch there while a *batch-shaped*
 * line would be accepted as a body of its own. Wrapping keeps "one line = one
 * envelope" exact: a line that carries an `events` member is an unexpected member
 * of the event object (`additional_properties` at `/events`), never a nested
 * batch.
 *
 * The 1 MiB transport cap is deliberately NOT part of this verdict: it is a
 * property of the body that travels, which `packBatches` measures on the exact
 * bytes before it validates (an oversized line is an `OversizedEventError`,
 * never an envelope rejection). The corpus keeps both halves as separate vectors
 * — `acc-05-max-bytes-exact` (a 1 MiB event, schema-valid) and
 * `rej-16-over-max-bytes` (`body_too_large`, shape-valid) — so applying the cap
 * here would make the validator disagree with one of them.
 */
function validateEnvelopeLine(schemas: ContractSchemaSet, line: string): EnvelopeDetail[] {
  let document: unknown;
  try {
    document = JSON.parse(line);
  } catch {
    // The registry's own token for an unparseable document (00 §9.3 `invalid_json`).
    return [{ path: '', reason: invalidJsonReason }];
  }

  const result = schemas.validateParsed(envelopeSchemaKey, { [eventsMember]: [document] });
  return result.errors.map((error) => ({
    path:
      error.path === wrappedItemPrefix
        ? ''
        : error.path.startsWith(`${wrappedItemPrefix}/`)
          ? error.path.slice(wrappedItemPrefix.length)
          : error.path,
    reason: error.reason,
  }));
}
