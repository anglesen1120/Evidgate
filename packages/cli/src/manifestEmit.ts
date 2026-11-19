// The manifest document `manifests build` writes, assembled from one execution per plan member
// in plan order. The shape is the minimum `manifest.v1.json` accepts: one target environment,
// one shard over every execution, and the matrix the verifier derives coverage from. Keys are
// written in one fixed order, so equal input always yields equal bytes.
import { canonicalizeJson } from './canonical.js';
import { CliError } from './errors.js';

/** The one target environment the schema requires; the plan carries no environment axis. */
const targetEnv = 'default';
/** One shard owns every execution: the plan is not sharded at build time. */
const shardId = 'shard-1';

/** One planned execution: the pinned case revision and the manifest-expressible variant. */
export interface PlannedExecution {
  readonly caseRevisionId: string;
  readonly params: Readonly<Record<string, string>>;
}

/** A `variant` or a `matrix.variants[]` entry; the schema allows nothing else under it. */
interface VariantDocument {
  readonly params: Readonly<Record<string, string>>;
}

/**
 * The manifest text (one trailing newline). `case_revision_ids` and `variants` keep the order
 * they first appear in the plan, and the variant axis is de-duplicated by canonical parameters,
 * so two cases of one variant share one axis entry.
 */
export function buildManifest(
  suiteRevisionId: string,
  executions: readonly PlannedExecution[],
): string {
  const caseRevisionIds: string[] = [];
  const variants: VariantDocument[] = [];
  const seenVariants = new Set<string>();
  const planned = executions.map((execution) => {
    if (!caseRevisionIds.includes(execution.caseRevisionId)) {
      caseRevisionIds.push(execution.caseRevisionId);
    }
    const variant = { params: execution.params };
    const key = canonicalizeJson(execution.params);
    if (!seenVariants.has(key)) {
      seenVariants.add(key);
      variants.push(variant);
    }
    return { case_revision_id: execution.caseRevisionId, variant, target_env: targetEnv };
  });
  assertMatrixCoverage(planned.length, caseRevisionIds.length, variants.length);
  return `${JSON.stringify(manifestDocument(suiteRevisionId, planned, caseRevisionIds, variants))}\n`;
}

/**
 * The verifier reads the plan as a full case × variant × environment product, so a revision whose
 * members are not one cannot be described by any manifest. Refusing here beats writing a file the
 * upload would reject as `matrix_not_covered`.
 */
function assertMatrixCoverage(
  executions: number,
  caseRevisions: number,
  variantCount: number,
): void {
  const expected = caseRevisions * variantCount;
  if (executions === expected) {
    return;
  }
  throw new CliError(
    `the execution plan is not a full matrix: ${executions} execution(s) where ${expected} are needed`,
    {
      code: 'manifest_plan_invalid',
      hint: 'every case of the revision must carry every variant; add the missing members or build for another revision',
    },
  );
}

/** The document in the schema's own key order, so the bytes are stable and reviewable. */
function manifestDocument(
  suiteRevisionId: string,
  planned: readonly object[],
  caseRevisionIds: readonly string[],
  variants: readonly VariantDocument[],
): object {
  return {
    schema_version: 1,
    suite_revision_id: suiteRevisionId,
    planned_executions: planned,
    matrix: {
      case_revision_ids: caseRevisionIds,
      variants,
      target_envs: [targetEnv],
    },
    shard_plan: {
      shards: [{ shard_id: shardId, from_index: 0, to_index: planned.length }],
    },
    env_needs: { target_envs: [{ target_env: targetEnv }] },
  };
}
