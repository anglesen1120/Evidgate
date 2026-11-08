import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { beforeAll, describe, expect, it } from 'vitest';

const probePath = fileURLToPath(new URL('./run-state.releases.probe.ts', import.meta.url));
const probeSource = [
  "import type { RunStateDto } from '../gen/types.gen.js';",
  "type Releases = Pick<RunStateDto, 'releases'>;",
  'const empty: Releases = { releases: [] };',
  'const absent: Releases = {};',
  'const nullable: Releases = { releases: null };',
  'const undefinedMember: Releases = { releases: undefined };',
  "const bound: Releases = { releases: [{ release_id: 'id', release_name: 'name', component_key: 'web' }] };",
  "const incomplete: Releases = { releases: [{ release_id: 'id' }] };",
  '',
  "type History = Pick<RunStateDto, 'state_history'>;",
  'const historyEmpty: History = { state_history: [] };',
  'const historyAbsent: History = {};',
  'const historyNull: History = { state_history: null };',
  'const historyUndefined: History = { state_history: undefined };',
  "const historyServed: History = { state_history: [{ from_state: 'queued', to_state: 'running', occurred_at: '2026-11-07T09:00:00Z' }] };",
  "const historyUnrecorded: History = { state_history: [{ from_state: 'queued', to_state: 'running' }] };",
  "const historyUnlistedState: History = { state_history: [{ from_state: 'shelved', to_state: 'running', occurred_at: '2026-11-07T09:00:00Z' }] };",
  '',
  "type Producers = Pick<RunStateDto, 'producers'>;",
  'const producersEmpty: Producers = { producers: [] };',
  'const producersAbsent: Producers = {};',
  'const producersUndefined: Producers = { producers: undefined };',
  'const producersNull: Producers = { producers: null };',
  "const producerFinalized: Producers = { producers: [{ producer_id: 'shard-1', identity_state: 'bound', finalize_status: 'finalized', last_sequence: 11, count: 11, expected: 11, stale_epoch_events: 0, after_cancel_events: 0 }] };",
  "const producerOpen: Producers = { producers: [{ producer_id: 'shard-1', identity_state: 'unbound', finalize_status: 'open', last_sequence: 0, count: 0, expected: null, stale_epoch_events: 0, after_cancel_events: 0 }] };",
  "const producerNoExpected: Producers = { producers: [{ producer_id: 'shard-1', identity_state: 'bound', finalize_status: 'finalized', last_sequence: 11, count: 11, stale_epoch_events: 0, after_cancel_events: 0 }] };",
  "const producerUnlistedIdentity: Producers = { producers: [{ producer_id: 'shard-1', identity_state: 'linked', finalize_status: 'finalized', last_sequence: 11, count: 11, expected: 11, stale_epoch_events: 0, after_cancel_events: 0 }] };",
].join('\n');

let errorLines: number[];

beforeAll(() => {
  const options: ts.CompilerOptions = {
    noEmit: true,
    strict: true,
    skipLibCheck: true,
    types: [],
    target: ts.ScriptTarget.ESNext,
    module: ts.ModuleKind.NodeNext,
    moduleResolution: ts.ModuleResolutionKind.NodeNext,
  };
  const host = ts.createCompilerHost(options);
  const getSourceFile = host.getSourceFile.bind(host);
  host.getSourceFile = (path, languageVersion, onError, shouldCreateNewSourceFile) =>
    path === probePath
      ? ts.createSourceFile(path, probeSource, languageVersion, true)
      : getSourceFile(path, languageVersion, onError, shouldCreateNewSourceFile);
  const program = ts.createProgram([probePath], options, host);
  const errors = ts
    .getPreEmitDiagnostics(program)
    .filter((diagnostic) => diagnostic.category === ts.DiagnosticCategory.Error);
  const otherErrors = errors.filter((diagnostic) => diagnostic.file?.fileName !== probePath);
  expect(
    otherErrors.map((diagnostic) => ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n')),
  ).toEqual([]);
  const source = program.getSourceFile(probePath);
  if (source === undefined) {
    throw new Error('The compiler did not load the RunStateDto assignment probe');
  }
  errorLines = errors.map(
    (diagnostic) => ts.getLineAndCharacterOfPosition(source, diagnostic.start ?? 0).line,
  );
});

describe('RunStateDto release member', () => {
  it('accepts an explicitly empty releases array', () => {
    expect(errorLines).not.toContain(2);
  });

  it('rejects an absent releases member', () => {
    expect(errorLines).toContain(3);
  });

  it('rejects null instead of an array', () => {
    expect(errorLines).toContain(4);
  });

  it('rejects an undefined releases member', () => {
    expect(errorLines).toContain(5);
  });

  it('accepts all three required release item members', () => {
    expect(errorLines).not.toContain(6);
  });

  it('rejects release items missing their name and component key', () => {
    expect(errorLines).toContain(7);
  });
});

describe('RunStateDto state history member', () => {
  it('accepts an explicitly empty history array', () => {
    expect(errorLines).not.toContain(10);
  });

  it('rejects an absent state_history member', () => {
    expect(errorLines).toContain(11);
  });

  it('accepts null as the unknown history, unlike releases', () => {
    expect(errorLines).not.toContain(12);
  });

  it('rejects an undefined state_history member', () => {
    expect(errorLines).toContain(13);
  });

  it('accepts an item with all three required members', () => {
    expect(errorLines).not.toContain(14);
  });

  it('rejects an item without its recorded time', () => {
    expect(errorLines).toContain(15);
  });

  it('rejects a state outside the run vocabulary', () => {
    expect(errorLines).toContain(16);
  });
});

describe('RunStateDto producers member', () => {
  it('accepts an explicitly empty slot array', () => {
    expect(errorLines).not.toContain(19);
  });

  it('rejects an absent producers member', () => {
    expect(errorLines).toContain(20);
  });

  it('rejects an undefined producers member', () => {
    expect(errorLines).toContain(21);
  });

  it('rejects null, unlike state_history: an empty array is the only "no slots" answer', () => {
    expect(errorLines).toContain(22);
  });

  it('accepts a finalized slot with every required member', () => {
    expect(errorLines).not.toContain(23);
  });

  it('accepts a null expected while the slot is open', () => {
    expect(errorLines).not.toContain(24);
  });

  it('rejects a slot without its declared count', () => {
    expect(errorLines).toContain(25);
  });

  it('rejects an identity state outside the closed pair', () => {
    expect(errorLines).toContain(26);
  });
});
