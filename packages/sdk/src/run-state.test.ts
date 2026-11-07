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
    throw new Error('The compiler did not load the release assignment probe');
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
