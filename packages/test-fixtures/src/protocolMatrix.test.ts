// 04.7.1-T3.1 / AC6 (T42): the TypeScript half of the protocol compatibility
// matrix. The table lives in one corpus file,
// fixtures/contracts/protocol-matrix/negotiation.v1.json, read by this spec and
// by the xUnit sibling tests/Tenancy.IntegrationTests/ProtocolCompatibilityMatrixTests.cs.
// Each row is evaluated in both runtimes against the same document and the same
// server protocol, so flipping any expected verdict in the corpus fails both
// suites — the corpus is the contract.
//
// The document is validated with the CLI's real Ajv 8 runtime
// (ContractSchemaSet, packages/cli/src/validate.ts) — the same engine the parity
// spec (packages/cli/src/parity.test.ts) and the CLI use — and then negotiated by
// the in-spec reference negotiator below, the adapter-author reading of
// contracts/adapter/README.md §Negotiation rules. No expected verdict is encoded
// here: every expectation comes from the corpus row.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { loadContractSchemas } from '@evidgate/cli';

/** One server protocol: the majors it accepts and the minor it implements. */
interface ServerSpec {
  readonly supported_majors: readonly number[];
  readonly minor: number;
}

/** The declared grid axes; every combination must appear exactly once. */
interface GridAxes {
  readonly server_supported_majors: readonly (readonly number[])[];
  readonly server_minor: readonly number[];
  readonly adapter_major: readonly number[];
  readonly adapter_minor: readonly number[];
}

/** One `details[]` row of the 00 §9.2 error envelope. */
interface NegotiationDetail {
  readonly path: string;
  readonly reason: string;
}

/** Accepted: the minor relation and the unknown `supports` flags, ordinal-sorted. */
interface AcceptedExpected {
  readonly verdict: 'accepted';
  readonly minor: 'equal' | 'adapter_newer' | 'adapter_older';
  readonly unknown_supports_flags: readonly string[];
}

/** Rejected before negotiation: the adapter's major is not in the server's set. */
interface MajorRejectedExpected {
  readonly verdict: 'rejected';
  readonly code: 'unsupported_protocol_major';
  readonly details: readonly NegotiationDetail[];
}

/** Rejected by the schema: the ordinal-sorted distinct `details[].path` set. */
interface InvalidExpected {
  readonly verdict: 'rejected';
  readonly code: 'capability_invalid';
  readonly paths: readonly string[];
}

type Expected = AcceptedExpected | MajorRejectedExpected | InvalidExpected;

/** A grid row: the document is `base_document` with `protocol` replaced by `adapter`. */
interface GridRow {
  readonly id: string;
  readonly kind: 'grid';
  readonly why: string;
  readonly server: ServerSpec;
  readonly adapter: { readonly major: number; readonly minor: number };
  readonly expected: Expected;
}

/** A hand-authored row: the document is used verbatim. */
interface DocumentRow {
  readonly id: string;
  readonly kind: 'unknown_field' | 'precedence';
  readonly why: string;
  readonly server: ServerSpec;
  readonly document: unknown;
  readonly expected: Expected;
}

type MatrixRow = GridRow | DocumentRow;

/** The corpus file: the declared axes, the base document and every row. */
interface MatrixCorpus {
  readonly format: string;
  readonly comment: string;
  readonly base_document: Record<string, unknown>;
  readonly grid: GridAxes;
  readonly rows: readonly MatrixRow[];
}

const repoRoot = fileURLToPath(new URL('../../..', import.meta.url));
const corpusPath = join(repoRoot, 'fixtures/contracts/protocol-matrix/negotiation.v1.json');
const corpus = JSON.parse(readFileSync(corpusPath, 'utf8')) as MatrixCorpus;
const schemas = loadContractSchemas(join(repoRoot, 'contracts'));

/** Ordinal comparison — the server sorts with `StringComparer.Ordinal`, never a locale collation. */
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/** The `supports` flags protocol v1.0 defines; every other flag is unknown (AC3). */
const knownSupportsFlags: Record<string, true> = {
  discovery: true,
  step_events: true,
  retry: true,
  cancel: true,
  capture_control: true,
};

type MinorRelation = 'equal' | 'adapter_newer' | 'adapter_older';

type Negotiation =
  | {
      readonly accepted: true;
      readonly minor: MinorRelation;
      readonly unknownSupportsFlags: readonly string[];
    }
  | {
      readonly accepted: false;
      readonly code: 'unsupported_protocol_major';
      readonly details: readonly NegotiationDetail[];
    };

/**
 * The adapter-author reading of contracts/adapter/README.md §Negotiation rules,
 * mirroring ProtocolNegotiator.cs: a major outside the server's set is rejected
 * with the adapter's major and the server's supported majors (ascending, comma
 * joined); otherwise the minor relation is equal / adapter_newer / adapter_older
 * and the unknown `supports` flags are the declared keys outside the known set,
 * ordinal-sorted. Pure: it reads the document and the server, never the corpus.
 */
function negotiate(document: Record<string, unknown>, server: ServerSpec): Negotiation {
  const protocol = document['protocol'] as { major: number; minor: number };
  const supports = document['supports'] as Record<string, boolean>;

  if (!server.supported_majors.includes(protocol.major)) {
    return {
      accepted: false,
      code: 'unsupported_protocol_major',
      details: [
        { path: '/protocol/major', reason: `adapter_major=${protocol.major}` },
        {
          path: '/protocol/major',
          reason: `supported_majors=${[...server.supported_majors].sort((a, b) => a - b).join(',')}`,
        },
      ],
    };
  }

  const minor: MinorRelation =
    protocol.minor === server.minor
      ? 'equal'
      : protocol.minor > server.minor
        ? 'adapter_newer'
        : 'adapter_older';

  const unknownSupportsFlags = Object.keys(supports)
    .filter((flag) => knownSupportsFlags[flag] !== true)
    .sort(ordinal);

  return { accepted: true, minor, unknownSupportsFlags };
}

/** The observed outcome of one row, in the same shape as the corpus `expected`. */
type Actual =
  | {
      readonly verdict: 'accepted';
      readonly minor: MinorRelation;
      readonly unknownSupportsFlags: readonly string[];
    }
  | {
      readonly verdict: 'rejected';
      readonly code: 'unsupported_protocol_major';
      readonly details: readonly NegotiationDetail[];
    }
  | {
      readonly verdict: 'rejected';
      readonly code: 'capability_invalid';
      readonly paths: readonly string[];
    };

/** The document under test: grid rows derive it from `base_document`; others carry it verbatim. */
function documentOf(row: MatrixRow): Record<string, unknown> {
  if (row.kind === 'grid') {
    return {
      ...corpus.base_document,
      protocol: { major: row.adapter.major, minor: row.adapter.minor },
    };
  }
  return row.document as Record<string, unknown>;
}

/**
 * Evaluation order mirrors RegisterAdapterCapability (04.7.1-T2.2): validate the
 * document against capability.v1 first (a schema failure is `capability_invalid`
 * and negotiation never runs), then bind, then negotiate.
 */
function evaluate(row: MatrixRow): Actual {
  const document = documentOf(row);

  const validation = schemas.validateParsed('capability.v1', document);
  if (validation.errors.length > 0) {
    const paths = [...new Set(validation.errors.map((error) => error.path))].sort(ordinal);
    return { verdict: 'rejected', code: 'capability_invalid', paths };
  }

  const negotiation = negotiate(document, row.server);
  if (!negotiation.accepted) {
    return { verdict: 'rejected', code: negotiation.code, details: negotiation.details };
  }

  return {
    verdict: 'accepted',
    minor: negotiation.minor,
    unknownSupportsFlags: negotiation.unknownSupportsFlags,
  };
}

/** The key of one grid combination: server majors + minor × adapter major + minor. */
function combinationKey(
  majors: readonly number[],
  minor: number,
  adapterMajor: number,
  adapterMinor: number,
): string {
  return `${majors.join('+')}|${minor}|${adapterMajor}|${adapterMinor}`;
}

describe.each(corpus.rows)('$id', (row) => {
  it('matches the corpus verdict', () => {
    const actual = evaluate(row);

    switch (row.expected.verdict) {
      case 'accepted': {
        expect(actual).toEqual({
          verdict: 'accepted',
          minor: row.expected.minor,
          unknownSupportsFlags: row.expected.unknown_supports_flags,
        });

        // AC3: every declared `supports` key is preserved — the known flags are
        // kept and the unknown ones are reported verbatim, never dropped.
        if (actual.verdict === 'accepted') {
          const declared = Object.keys(documentOf(row)['supports'] as Record<string, unknown>);
          const accounted = new Set([
            ...declared.filter((flag) => knownSupportsFlags[flag] === true),
            ...actual.unknownSupportsFlags,
          ]);
          expect([...accounted].sort(ordinal)).toEqual([...declared].sort(ordinal));
        }
        break;
      }
      case 'rejected': {
        if (row.expected.code === 'unsupported_protocol_major') {
          expect(actual).toEqual({
            verdict: 'rejected',
            code: 'unsupported_protocol_major',
            details: row.expected.details,
          });
        } else {
          expect(actual).toEqual({
            verdict: 'rejected',
            code: 'capability_invalid',
            paths: row.expected.paths,
          });
        }
        break;
      }
      default:
        // A verdict the corpus contract does not define must fail, never pass silently.
        expect.unreachable(`${row.id}: unknown expected verdict ${JSON.stringify(row.expected)}`);
    }
  });
});

describe('corpus shape and grid coverage (AC6)', () => {
  it('covers the declared grid exactly once and keeps the contract shape', () => {
    // Top-level members are exactly the contract's.
    expect(Object.keys(corpus).sort(ordinal)).toEqual(
      ['base_document', 'comment', 'format', 'grid', 'rows'].sort(ordinal),
    );
    expect(corpus.format).toBe('evidgate.protocol-matrix.v1');
    expect(corpus.rows.length).toBeGreaterThan(0);

    // Row ids are unique, and every row documents its expected outcome.
    const ids = corpus.rows.map((row) => row.id);
    expect(new Set(ids).size).toBe(ids.length);

    for (const row of corpus.rows) {
      expect(row.why.length, `${row.id}: 'why' documents the expected outcome`).toBeGreaterThan(0);

      // Kind-specific members: a grid row carries `adapter` and no `document`; a
      // hand-authored row carries `document` and no `adapter`.
      const keys = Object.keys(row).sort(ordinal);
      expect(keys).toEqual(
        row.kind === 'grid'
          ? ['adapter', 'expected', 'id', 'kind', 'server', 'why']
          : ['document', 'expected', 'id', 'kind', 'server', 'why'],
      );

      // `expected` carries exactly the members of its verdict, as in the .NET reader.
      const expectedMembers = Object.keys(row.expected).sort(ordinal);
      const verdictMembers =
        row.expected.verdict === 'accepted'
          ? ['minor', 'unknown_supports_flags', 'verdict']
          : row.expected.verdict === 'rejected' && row.expected.code === 'capability_invalid'
            ? ['code', 'paths', 'verdict']
            : row.expected.verdict === 'rejected' &&
                row.expected.code === 'unsupported_protocol_major'
              ? ['code', 'details', 'verdict']
              : null;
      expect(verdictMembers, `${row.id}: unknown expected verdict/code`).not.toBeNull();
      expect(expectedMembers, `${row.id}: expected members`).toEqual(verdictMembers);
    }

    // Every combination of the four declared axes appears exactly once.
    const expectedKeys = new Set<string>();
    for (const majors of corpus.grid.server_supported_majors) {
      for (const minor of corpus.grid.server_minor) {
        for (const adapterMajor of corpus.grid.adapter_major) {
          for (const adapterMinor of corpus.grid.adapter_minor) {
            expectedKeys.add(combinationKey(majors, minor, adapterMajor, adapterMinor));
          }
        }
      }
    }
    expect(expectedKeys.size).toBe(27);

    const seen = new Map<string, number>();
    for (const row of corpus.rows) {
      if (row.kind !== 'grid') continue;
      const key = combinationKey(
        row.server.supported_majors,
        row.server.minor,
        row.adapter.major,
        row.adapter.minor,
      );
      seen.set(key, (seen.get(key) ?? 0) + 1);
    }
    expect([...seen.keys()].sort(ordinal)).toEqual([...expectedKeys].sort(ordinal));
    for (const [key, count] of seen) {
      expect(count, `grid combination ${key} appears ${count} times`).toBe(1);
    }
  });
});
