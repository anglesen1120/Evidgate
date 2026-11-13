// The `execution_key` of one planned execution: lowercase-hex SHA-256 over three length-prefixed
// UTF-8 fields, in the order `case_id`, `variant_hash`, `target_env`. The byte layout is defined
// once, in the server's `ExecutionKey` (Orchestrator/Domain/ExecutionKey.cs); this is its port,
// pinned to it by fixtures/execution-key/vectors.json, which both runtimes assert. The server
// derives the key itself and refuses any other, so a wrong value here is an ingest refusal,
// never a silently accepted identity.
import { createHash } from 'node:crypto';

export function computeExecutionKey(
  caseId: string,
  variantHash: string,
  targetEnv: string,
): string {
  const hash = createHash('sha256');
  for (const field of [caseId, variantHash, targetEnv]) {
    const bytes = Buffer.from(field, 'utf8');
    const length = Buffer.alloc(4);
    length.writeUInt32BE(bytes.length);
    hash.update(length).update(bytes);
  }
  return hash.digest('hex');
}
