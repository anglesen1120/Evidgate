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
// Why a dependency edge keeps only its ids: an edge names member ids of the same revision (the
// table's foreign keys make that structural), and the screen shows what the wire carries, so
// resolving each end to its member would build a second copy the template never reads.

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

/** One served dependency edge in arrival order; each end is the member id the wire carries. */
export interface DependencyRow {
  readonly parentMemberId: string;
  readonly childMemberId: string;
}

/** One served member as the flat table shows it, in arrival (ordinal) order. */
export interface FlatMemberRow {
  readonly memberId: string;
  readonly caseRevisionId: string;
  readonly variantHash: string;
  /** The member's parameter document as `key=value` strings, sorted; empty when it has none. */
  readonly params: readonly string[];
  readonly required: boolean;
  readonly ordinal: number;
}

/**
 * How the members are drawn. A matrix draws every row against every column, so its size is that
 * product; once it passes `MATRIX_CELL_LIMIT` the flat list is the only shape that stays readable.
 */
export type MemberLayout = 'matrix' | 'flat';

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
  readonly layout: MemberLayout;
  /** Served members for the narrow-screen records and the oversized-grid flat table. */
  readonly flatMembers: readonly FlatMemberRow[];
  readonly dependencies: readonly DependencyRow[];
  readonly strategy: StrategySummary;
  /**
   * Served members the matrix does not draw because their case revision already holds their variant
   * hash — the second and later members of one cell, so a cell holding three members leaves two
   * out. Counted instead of dropped silently: the wire served them, so a revision that repeats a
   * cell says so on the screen. The flat table lists every served member, which is why only the
   * matrix carries that note.
   */
  readonly duplicateMembers: number;
}

/**
 * The members, edges and strategy document the view reads from a served revision. The strategy
 * document is read through `strategySummary`, which accepts the `null` a served revision can carry
 * even though the contract types the field as an object, so the screen says `unavailable` instead
 * of throwing.
 */
export type CompositionSource = Pick<SuiteRevisionDto, 'members' | 'edges' | 'shard_strategy'>;

/** Upper bound the shard strategy schema sets on `count`; a larger value is not a shard count. */
const MAX_SHARD_COUNT = 256;

/** The most cells one matrix may draw; above it the flat member table takes over. */
export const MATRIX_CELL_LIMIT = 2500;

/** Rows one `Show more` press appends to a chunked list. */
export const LIST_CHUNK = 200;

export function compositionView(revision: CompositionSource): SuiteCompositionView {
  const columns = variantColumns(revision.members);
  const grid = compositionRows(revision.members);
  const layout: MemberLayout =
    grid.rows.length * columns.length > MATRIX_CELL_LIMIT ? 'flat' : 'matrix';
  return {
    columns,
    rows: grid.rows,
    layout,
    flatMembers: flatMemberRows(revision.members),
    dependencies: revision.edges.map((edge) => dependencyRow(edge)),
    strategy: strategySummary(revision.shard_strategy),
    duplicateMembers: grid.duplicateMembers,
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

/**
 * Rows sorted by case revision id, so a redraw of the same revision keeps its order, plus the count
 * of members the table cannot place.
 *
 * One cell holds one member, so a second member under a variant hash its case revision already
 * holds has nowhere to go. The first one keeps the cell — it is the member whose parameters the
 * variant column header already shows, so the cell and the header describe one member — and the
 * rest are counted rather than dropped silently: a revision that repeats a cell is a composition
 * the reader has to know about, and `duplicateMembers` is what lets the screen say so.
 */
function compositionRows(members: readonly SuiteMemberDto[]): {
  readonly rows: readonly CompositionRow[];
  readonly duplicateMembers: number;
} {
  const byCase = new Map<string, Map<string, SuiteMemberDto>>();
  let duplicateMembers = 0;
  for (const member of members) {
    const row = byCase.get(member.case_revision_id) ?? new Map<string, SuiteMemberDto>();
    byCase.set(member.case_revision_id, row);
    if (row.has(member.variant_hash)) {
      duplicateMembers += 1;
      continue;
    }
    row.set(member.variant_hash, member);
  }
  const rows = [...byCase.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([caseRevisionId, byVariant]) => ({ caseRevisionId, byVariant }));
  return { rows, duplicateMembers };
}

function dependencyRow(edge: SuiteEdgeDto): DependencyRow {
  return {
    parentMemberId: edge.parent_member_id,
    childMemberId: edge.child_member_id,
  };
}

/** Every served member as one flat row, in the order the members arrive (their ordinal order). */
function flatMemberRows(members: readonly SuiteMemberDto[]): readonly FlatMemberRow[] {
  return members.map((member) => ({
    memberId: member.id,
    caseRevisionId: member.case_revision_id,
    variantHash: member.variant_hash,
    params: variantParameters(member.params),
    required: member.required,
    ordinal: member.ordinal,
  }));
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

export function strategySummary(
  strategy: Record<string, unknown> | null | undefined,
): StrategySummary {
  // A missing document is unreadable, not "no sharding": saying `none` would claim the revision
  // declares no sharding when the wire said nothing the screen can read.
  if (strategy === null || strategy === undefined) return { kind: 'unavailable' };
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
