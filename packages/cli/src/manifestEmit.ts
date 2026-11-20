// The manifest document `manifests build` writes, assembled from one execution per plan member
// × target environment, in plan order and then target order. The shape is the minimum
// `manifest.v1` accepts: the requested target environments, a shard plan that partitions every
// execution, and the matrix the verifier derives coverage from. Keys are written in one fixed
// order, so equal input always yields equal bytes.
import { canonicalizeJson } from './canonical.js';
import { CliError } from './errors.js';
import { shardDocuments, type ShardCount, type ShardDocument } from './manifestShards.js';

/** The one target environment a build declares when none is requested. */
export const defaultTargetEnv = 'default';

/** One planned execution: the pinned case revision and the manifest-expressible variant. */
export interface PlannedExecution {
  readonly caseRevisionId: string;
  readonly params: Readonly<Record<string, string>>;
}

/** The optional axes `manifests build` adds to the plan; absent values keep the old output. */
export interface ManifestOptions {
  readonly targetEnvs?: readonly string[];
  readonly environmentRevisionId?: string;
  readonly shards?: ShardCount;
}

/** A `variant` or a `matrix.variants[]` entry; the schema allows nothing else under it. */
interface VariantDocument {
  readonly params: Readonly<Record<string, string>>;
}

/** One `planned_executions[]` entry. */
interface PlannedDocument {
  readonly case_revision_id: string;
  readonly variant: VariantDocument;
  readonly target_env: string;
}

/**
 * The planned executions plus the two matrix axes they imply. `case_revision_ids` and
 * `variants` keep the order they first appear in the plan, and the variant axis is
 * de-duplicated by canonical parameters, so two cases of one variant share one axis entry.
 */
interface ManifestAxes {
  readonly planned: readonly PlannedDocument[];
  readonly caseRevisionIds: readonly string[];
  readonly variants: readonly VariantDocument[];
}

/**
 * The manifest text (one trailing newline) for one suite revision. A plan member becomes one
 * execution per target environment; the shard plan partitions all of them.
 */
export function buildManifest(
  suiteRevisionId: string,
  executions: readonly PlannedExecution[],
  options: ManifestOptions = {},
): string {
  const targetEnvs = options.targetEnvs ?? [defaultTargetEnv];
  const axes = manifestAxes(executions, targetEnvs);
  assertMatrixCoverage(
    axes.planned.length,
    axes.caseRevisionIds.length,
    axes.variants.length,
    targetEnvs.length,
  );
  const shards = shardDocuments(axes.planned.length, options.shards ?? 1);
  const document = manifestDocument({
    suiteRevisionId,
    axes,
    targetEnvs,
    shards,
    ...(options.environmentRevisionId === undefined
      ? {}
      : { environmentRevisionId: options.environmentRevisionId }),
  });
  return `${JSON.stringify(document)}\n`;
}

/** Expands each plan member across the target environments, in member order then target order. */
function manifestAxes(
  executions: readonly PlannedExecution[],
  targetEnvs: readonly string[],
): ManifestAxes {
  const caseRevisionIds: string[] = [];
  const seenCaseRevisions = new Set<string>();
  const variants: VariantDocument[] = [];
  const seenVariants = new Set<string>();
  const planned: PlannedDocument[] = [];
  for (const execution of executions) {
    if (!seenCaseRevisions.has(execution.caseRevisionId)) {
      seenCaseRevisions.add(execution.caseRevisionId);
      caseRevisionIds.push(execution.caseRevisionId);
    }
    const variant = { params: execution.params };
    const key = canonicalizeJson(execution.params);
    if (!seenVariants.has(key)) {
      seenVariants.add(key);
      variants.push(variant);
    }
    for (const targetEnv of targetEnvs) {
      planned.push({
        case_revision_id: execution.caseRevisionId,
        variant,
        target_env: targetEnv,
      });
    }
  }
  return { planned, caseRevisionIds, variants };
}

/**
 * The verifier reads the plan as a full case × variant × environment product, so a revision
 * whose members are not one cannot be described by any manifest. Refusing here beats writing a
 * file the upload would reject as `matrix_not_covered`.
 */
function assertMatrixCoverage(
  executions: number,
  caseRevisions: number,
  variantCount: number,
  targetEnvs: number,
): void {
  const expected = caseRevisions * variantCount * targetEnvs;
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

interface ManifestDocumentInput {
  readonly suiteRevisionId: string;
  readonly axes: ManifestAxes;
  readonly targetEnvs: readonly string[];
  readonly shards: readonly ShardDocument[];
  readonly environmentRevisionId?: string;
}

/** The document in the schema's own key order, so the bytes are stable and reviewable. */
function manifestDocument(input: ManifestDocumentInput): object {
  const revision = input.environmentRevisionId;
  return {
    schema_version: 1,
    suite_revision_id: input.suiteRevisionId,
    ...(revision === undefined ? {} : { environment_revision_id: revision }),
    planned_executions: input.axes.planned,
    matrix: {
      case_revision_ids: input.axes.caseRevisionIds,
      variants: input.axes.variants,
      target_envs: input.targetEnvs,
    },
    shard_plan: { shards: input.shards },
    env_needs: { target_envs: input.targetEnvs.map((targetEnv) => ({ target_env: targetEnv })) },
  };
}
