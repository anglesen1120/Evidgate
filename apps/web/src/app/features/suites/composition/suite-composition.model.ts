// The composition view's shape, derived from one served suite revision.
//
// Why it is not in the component: the matrix, the strategy summary and the dependency list are
// pure projections of the read, so they are testable without a DOM and the template stays free of
// index arithmetic.
//
// Why nothing is derived from names: `GET /v1/suite-revisions/{r}` carries case revision ids,
// variant hashes, parameter documents and member ids, and no case name, suite name or warning
// text. The view therefore shows exactly those identifiers. A cell holds the served member, so
// `required`, `ordinal` and the member id all come from the wire unchanged.
//
// Why a failed lookup is `null` rather than a guess: a dependency edge names member ids of the
// same revision (the table's foreign keys make that structural), so a lookup that misses is a
// wire the client does not understand, and the view keeps the raw id instead of inventing a name.

import type { SuiteEdgeDto, SuiteMemberDto, SuiteRevisionDto } from '@evidgate/sdk';

/** One variant column: every member of the revision whose variant hash matches it. */
export interface VariantColumn {
  readonly variantHash: string;
  /** The member's parameter document as `key=value` strings in key order; empty when it has none. */
  readonly params: readonly string[];
}

/** One matrix row: every member of the revision for one case revision, keyed by variant column. */
export interface CompositionRow {
  readonly caseRevisionId: string;
  /** Members of this case revision by the variant hash of their column; `get` misses are empty cells. */
  readonly byVariant: ReadonlyMap<string, SuiteMemberDto>;
}

/** One served dependency edge, with each end resolved to its member where the revision holds it. */
export interface DependencyRow {
  readonly parentMemberId: string;
  readonly parent: SuiteMemberDto | null;
  readonly childMemberId: string;
  readonly child: SuiteMemberDto | null;
}

/**
 * The shard strategy as the screen can state it. `unavailable` is a real state, not a placeholder:
 * the contract types `shard_strategy` as an untyped object, so the client narrows it, and a
 * document it cannot read must say so instead of showing `0`, `none` or a blank.
 */
export type StrategySummary =
  | { readonly kind: 'none' }
  | {
      readonly kind: 'count';
      readonly count: number;
      /** `null` when the document declares no balancing, which the schema allows. */
      readonly balanceBy: 'duration' | 'count' | null;
    }
  | { readonly kind: 'unavailable' };

export interface SuiteCompositionView {
  readonly columns: readonly VariantColumn[];
  readonly rows: readonly CompositionRow[];
  readonly dependencies: readonly DependencyRow[];
  readonly strategy: StrategySummary;
}

/** The members, edges and strategy document the view reads from a served revision. */
export type CompositionSource = Pick<SuiteRevisionDto, 'members' | 'edges' | 'shard_strategy'>;

/** Upper bound the shard strategy schema sets on `count`; a larger value is not a shard count. */
const MAX_SHARD_COUNT = 256;

export function compositionView(revision: CompositionSource): SuiteCompositionView {
  const columns = variantColumns(revision.members);
  const membersById = new Map(revision.members.map((member) => [member.id, member]));
  return {
    columns,
    rows: compositionRows(revision.members),
    dependencies: revision.edges.map((edge) => dependencyRow(edge, membersById)),
    strategy: strategySummary(revision.shard_strategy),
  };
}

/** Columns in the order the members arrive (ordinal order), one per distinct variant hash. */
function variantColumns(members: readonly SuiteMemberDto[]): readonly VariantColumn[] {
  const known = new Set<string>();
  const columns: VariantColumn[] = [];
  for (const member of members) {
    if (known.has(member.variant_hash)) continue;
    known.add(member.variant_hash);
    columns.push({
      variantHash: member.variant_hash,
      params: variantParameters(member.params),
    });
  }
  return columns;
}

/** Rows sorted by case revision id, so a redraw of the same revision keeps its order. */
function compositionRows(members: readonly SuiteMemberDto[]): readonly CompositionRow[] {
  const byCase = new Map<string, Map<string, SuiteMemberDto>>();
  for (const member of members) {
    const row = byCase.get(member.case_revision_id) ?? new Map<string, SuiteMemberDto>();
    row.set(member.variant_hash, member);
    byCase.set(member.case_revision_id, row);
  }
  return [...byCase.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([caseRevisionId, byVariant]) => ({ caseRevisionId, byVariant }));
}

function dependencyRow(
  edge: SuiteEdgeDto,
  membersById: ReadonlyMap<string, SuiteMemberDto>,
): DependencyRow {
  return {
    parentMemberId: edge.parent_member_id,
    parent: membersById.get(edge.parent_member_id) ?? null,
    childMemberId: edge.child_member_id,
    child: membersById.get(edge.child_member_id) ?? null,
  };
}

/**
 * The parameter document as `key=value` text. The schema declares flat scalars, so a string,
 * number or boolean is shown as is; anything else is shown as its JSON text, because hiding it
 * would make two different variants look identical. Returns no entries for a non-object document.
 */
export function variantParameters(params: unknown): readonly string[] {
  if (!isParameterDocument(params)) return [];
  return Object.keys(params)
    .sort()
    .map((key) => `${key}=${scalarText(params[key])}`);
}

function scalarText(value: unknown): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return JSON.stringify(value) ?? '';
}

export function strategySummary(strategy: Record<string, unknown>): StrategySummary {
  const kind = strategy['kind'];
  if (kind === 'none') return { kind: 'none' };
  if (kind !== 'count') return { kind: 'unavailable' };
  const count = strategy['count'];
  if (typeof count !== 'number' || !Number.isInteger(count)) return { kind: 'unavailable' };
  if (count < 1 || count > MAX_SHARD_COUNT) return { kind: 'unavailable' };
  const balanceBy = strategy['balance_by'];
  if (balanceBy === undefined) return { kind: 'count', count, balanceBy: null };
  if (balanceBy !== 'duration' && balanceBy !== 'count') return { kind: 'unavailable' };
  return { kind: 'count', count, balanceBy };
}

/**
 * The parameter document is typed `unknown` on the wire (`JsonElement`), so the client narrows it
 * here, once, for the one shape the schema declares: a flat object of scalars. This predicate is
 * the parameter document's data contract, which is why it is not a general `isRecord`.
 */
function isParameterDocument(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
