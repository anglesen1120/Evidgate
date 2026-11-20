/*
 * Syntax cannot follow a Response passed to another module's helper, dynamic property names,
 * aliases through parameters or returns, or eval; a .body handed to a stream helper is out of
 * scope. Only one identifier-to-identifier alias hop is resolved, and any receiver is flagged.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

const sourceRoot = import.meta.dirname;
const bodyMethods = new Set([
  'text',
  'json',
  'arrayBuffer',
  'blob',
  'formData',
  'bytes',
  'getReader',
]);
const streamMethods = new Set(['values', 'pipeTo', 'pipeThrough']);
type Finding = { line: number; function: string; kind: string };
type Bindings = Map<ts.Node, Map<string, ts.Expression | undefined>>;

function unwrap(expression: ts.Expression): ts.Expression {
  while (
    ts.isParenthesizedExpression(expression) ||
    ts.isAsExpression(expression) ||
    ts.isTypeAssertionExpression(expression) ||
    ts.isNonNullExpression(expression) ||
    ts.isSatisfiesExpression(expression) ||
    ts.isAwaitExpression(expression)
  ) {
    expression = expression.expression;
  }
  return expression;
}

function member(expression: ts.Expression): { name: string; receiver: ts.Expression } | undefined {
  expression = unwrap(expression);
  if (ts.isPropertyAccessExpression(expression)) {
    return { name: expression.name.text, receiver: expression.expression };
  }
  if (
    ts.isElementAccessExpression(expression) &&
    ts.isStringLiteralLike(expression.argumentExpression)
  ) {
    return { name: expression.argumentExpression.text, receiver: expression.expression };
  }
  return undefined;
}

function scopeOf(node: ts.Node): ts.Node {
  let current: ts.Node = node;
  while (
    current.parent &&
    !ts.isSourceFile(current) &&
    !ts.isFunctionLike(current) &&
    !ts.isBlock(current)
  ) {
    current = current.parent;
  }
  return current;
}

function bindingNames(name: ts.BindingName): string[] {
  if (ts.isIdentifier(name)) {
    return [name.text];
  }
  return name.elements.flatMap((element) =>
    ts.isBindingElement(element) ? bindingNames(element.name) : [],
  );
}

function collectBindings(source: ts.SourceFile): Bindings {
  const bindings: Bindings = new Map();
  function visit(node: ts.Node): void {
    if (ts.isVariableDeclaration(node) || ts.isParameter(node)) {
      const scope = scopeOf(node.parent);
      const names = bindings.get(scope) ?? new Map<string, ts.Expression | undefined>();
      for (const name of bindingNames(node.name)) {
        names.set(name, ts.isIdentifier(node.name) ? node.initializer : undefined);
      }
      bindings.set(scope, names);
    }
    ts.forEachChild(node, visit);
  }
  visit(source);
  return bindings;
}

function resolve(expression: ts.Expression, bindings: Bindings, hops = 1): ts.Expression {
  expression = unwrap(expression);
  if (!ts.isIdentifier(expression)) {
    return expression;
  }
  let scope: ts.Node | undefined = scopeOf(expression);
  while (scope) {
    const names = bindings.get(scope);
    if (names?.has(expression.text)) {
      const initializer = names.get(expression.text);
      if (!initializer) {
        return expression;
      }
      const value = unwrap(initializer);
      return ts.isIdentifier(value) && hops > 0 ? resolve(value, bindings, hops - 1) : value;
    }
    scope = scope.parent ? scopeOf(scope.parent) : undefined;
  }
  return expression;
}

function isBody(expression: ts.Expression, bindings: Bindings): boolean {
  return member(resolve(expression, bindings))?.name === 'body';
}

function isStream(expression: ts.Expression, bindings: Bindings): boolean {
  expression = resolve(expression, bindings);
  return (
    isBody(expression, bindings) ||
    (ts.isNewExpression(expression) &&
      ts.isIdentifier(expression.expression) &&
      expression.expression.text === 'ReadableStream')
  );
}

function isReader(expression: ts.Expression, bindings: Bindings): boolean {
  expression = resolve(expression, bindings);
  return ts.isCallExpression(expression) && member(expression.expression)?.name === 'getReader';
}

function callKind(call: ts.CallExpression, bindings: Bindings): string | undefined {
  let target = member(resolve(call.expression, bindings));
  let suffix = '';
  if (target && ['call', 'apply', 'bind'].includes(target.name)) {
    suffix = `.${target.name}`;
    target = member(resolve(target.receiver, bindings));
  }
  if (!target) {
    return undefined;
  }
  if (
    bodyMethods.has(target.name) ||
    (target.name === 'read' && isReader(target.receiver, bindings)) ||
    (streamMethods.has(target.name) && isStream(target.receiver, bindings))
  ) {
    return `${target.name}${suffix}`;
  }
  return undefined;
}

function enclosingFunction(node: ts.Node): string {
  let method: string | undefined;
  for (let current: ts.Node | undefined = node.parent; current; current = current.parent) {
    if (ts.isFunctionDeclaration(current) || ts.isFunctionExpression(current)) {
      if (current.name) {
        return current.name.text;
      }
    }
    if (
      (ts.isArrowFunction(current) || ts.isFunctionExpression(current)) &&
      ts.isVariableDeclaration(current.parent) &&
      ts.isIdentifier(current.parent.name)
    ) {
      return current.parent.name.text;
    }
    if (ts.isMethodDeclaration(current)) {
      method ??= current.name.getText();
    }
  }
  // Stream callbacks belong to their enclosing function's cap, not the callback's method name.
  return method ?? '<top-level>';
}

/**
 * The read a destructured key names. A computed key counts only as a string literal, so
 * `const { ['text']: read } = answer` is flagged while `[name]` and `[text]` — a dynamic name
 * that may hold anything — stay out of scope.
 */
function keyKind(name: ts.Node | undefined): string | undefined {
  if (name === undefined) {
    return undefined;
  }
  if (ts.isComputedPropertyName(name)) {
    const key = unwrap(name.expression);
    return ts.isStringLiteralLike(key) ? readKind(key.text) : undefined;
  }
  return ts.isIdentifier(name) || ts.isStringLiteralLike(name) ? readKind(name.text) : undefined;
}

function readKind(name: string): string | undefined {
  return bodyMethods.has(name) || name === 'body' ? `destructure:${name}` : undefined;
}

function destructuredKinds(node: ts.Node): string[] {
  if (ts.isObjectBindingPattern(node)) {
    return node.elements.flatMap((element) => {
      const kind = keyKind(element.propertyName ?? element.name);
      return kind ? [kind] : [];
    });
  }
  // A statement-level `({ body } = x)` wraps the pattern, so the assignment is one level up.
  let parent: ts.Node | undefined = node.parent;
  while (parent && ts.isParenthesizedExpression(parent)) {
    parent = parent.parent;
  }
  if (
    !parent ||
    !ts.isObjectLiteralExpression(node) ||
    !ts.isBinaryExpression(parent) ||
    unwrap(parent.left) !== node ||
    parent.operatorToken.kind !== ts.SyntaxKind.EqualsToken
  ) {
    return [];
  }
  return node.properties.flatMap((property) => {
    const kind = ts.isSpreadAssignment(property) ? undefined : keyKind(property.name);
    return kind ? [kind] : [];
  });
}

export function findWholeBodyReads(source: string, fileName: string): Finding[] {
  const tree = ts.createSourceFile(
    fileName,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  const bindings = collectBindings(tree);
  const findings: Finding[] = [];
  function visit(node: ts.Node): void {
    const kinds = destructuredKinds(node);
    if (ts.isCallExpression(node)) {
      const kind = callKind(node, bindings);
      if (kind) {
        kinds.push(kind);
      }
    }
    if (ts.isForOfStatement(node) && node.awaitModifier && isBody(node.expression, bindings)) {
      kinds.push('for-await:body');
    }
    for (const kind of kinds) {
      findings.push({
        line: tree.getLineAndCharacterOfPosition(node.getStart(tree)).line + 1,
        function: enclosingFunction(node),
        kind,
      });
    }
    ts.forEachChild(node, visit);
  }
  visit(tree);
  return findings;
}

const allowed: Readonly<Record<string, string>> = {
  'boundedResponse.ts::boundedResponse': 'the byte-counted stream and its underlying reader loop',
  'boundedResponse.ts::readBoundedText': 'whole text is read only after the bounded stream wrapper',
  'commands/gate.ts::boundedErrorBody': 'the non-2xx error-body reader cuts the answer at 64 KiB',
};

function productionFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === 'scanFixtures' ? [] : productionFiles(path);
    }
    const isTest = /\.(?:test|testing)\.ts$/.test(entry.name);
    return entry.name.endsWith('.ts') && !isTest ? [path] : [];
  });
}

describe('response body reads', () => {
  it.each(['alias', 'newResponse', 'forAwait', 'destructured'])(
    'flags the %s.fixture.txt bypass',
    (name) => {
      const file = `${name}.fixture.txt`;
      const source = readFileSync(join(sourceRoot, 'scanFixtures', file), 'utf8');
      expect(findWholeBodyReads(source, file), file).not.toEqual([]);
    },
  );

  it('accepts clean.fixture.txt', () => {
    const file = 'clean.fixture.txt';
    const source = readFileSync(join(sourceRoot, 'scanFixtures', file), 'utf8');
    expect(findWholeBodyReads(source, file), file).toEqual([]);
  });

  it.each([
    ['arbitrary receiver', 'async function load() { await payload.text(); }', 'load', 'text'],
    ['nested receiver', 'result.response.json()', '<top-level>', 'json'],
    ['new response', 'new Response(stream).arrayBuffer()', '<top-level>', 'arrayBuffer'],
    ['new request', 'new Request(url).blob()', '<top-level>', 'blob'],
    ['new receiver', 'new Container().formData()', '<top-level>', 'formData'],
    ['optional call', 'incoming?.bytes?.()', '<top-level>', 'bytes'],
    ['element call', "incoming['text']()", '<top-level>', 'text'],
    ['call indirection', 'incoming.text.call(incoming)', '<top-level>', 'text.call'],
    ['apply indirection', "incoming['json'].apply(incoming)", '<top-level>', 'json.apply'],
    ['manual reader', 'incoming.body.getReader()', '<top-level>', 'getReader'],
    ['body destructure', 'const { body: stream } = incoming;', '<top-level>', 'destructure:body'],
    ['method destructure', 'const { text: read } = incoming;', '<top-level>', 'destructure:text'],
    ['assignment destructure', '({ json: read } = incoming);', '<top-level>', 'destructure:json'],
    [
      'computed destructure',
      "const { ['text']: read } = incoming;",
      '<top-level>',
      'destructure:text',
    ],
    [
      'computed assignment destructure',
      "({ ['body']: stream } = incoming);",
      '<top-level>',
      'destructure:body',
    ],
    [
      'direct iteration',
      'for await (const chunk of incoming.body) {}',
      '<top-level>',
      'for-await:body',
    ],
    [
      'awaited iteration source',
      'for await (const chunk of await incoming.body) {}',
      '<top-level>',
      'for-await:body',
    ],
    ['awaited stream receiver', '(await incoming.body).values()', '<top-level>', 'values'],
    [
      'awaited non-null iteration source',
      'for await (const chunk of (await incoming.body)!) {}',
      '<top-level>',
      'for-await:body',
    ],
    [
      'aliased iteration',
      'const stream = incoming.body; let copy = stream; for await (const chunk of copy) {}',
      '<top-level>',
      'for-await:body',
    ],
    [
      'reader alias',
      'const reader = incoming.body.getReader(); let copy = reader; copy.read();',
      '<top-level>',
      'read',
    ],
    ['stream iteration', 'const stream = incoming.body; stream.values();', '<top-level>', 'values'],
    ['stream constructor', 'new ReadableStream().pipeTo(destination)', '<top-level>', 'pipeTo'],
    ['arrow declaration', 'const load = async () => incoming.text();', 'load', 'text'],
    [
      'nested callback',
      'function load() { return new ReadableStream({ pull() { incoming.text(); } }); }',
      'load',
      'text',
    ],
  ])('flags %s syntax', (_name, source, fn, kind) => {
    expect(findWholeBodyReads(source, 'control.ts')).toContainEqual({
      line: 1,
      function: fn,
      kind,
    });
  });

  it('preserves line numbers without treating comments or strings as code', () => {
    const source = '// incoming.text()\nfunction load() {\n  return incoming.json();\n}';
    expect(findWholeBodyReads(source, 'control.ts')).toEqual([
      { line: 3, function: 'load', kind: 'json' },
    ]);
    expect(findWholeBodyReads('"incoming.text()"; JSON.parse("{}");', 'control.ts')).toEqual([]);
  });

  it('does not leak aliases across functions or shadowing blocks', () => {
    const source = `
      function first() { const stream = incoming.body; }
      async function second(stream) { for await (const chunk of stream) {} }
      async function third() {
        const stream = incoming.body;
        { const stream = items; for await (const chunk of stream) {} }
      }
    `;
    expect(findWholeBodyReads(source, 'control.ts')).toEqual([]);
  });

  it('keeps helper arguments and unrelated readers outside this syntax-only scope', () => {
    expect(
      findWholeBodyReads('readHelper(incoming.body); await file.read();', 'control.ts'),
    ).toEqual([]);
  });

  it('keeps dynamic keys and non-body awaited sources out of scope', () => {
    const source = [
      'const { [name]: read } = incoming;',
      "const text = 'other';",
      'const { [text]: read } = incoming;',
      "const body = 'other';",
      'const { [body]: stream } = incoming;',
      'for await (const chunk of await items) {}',
    ].join(' ');

    expect(findWholeBodyReads(source, 'control.ts')).toEqual([]);
  });

  it('finds no production reads outside the three used, reasoned function exceptions', () => {
    const paths = productionFiles(sourceRoot);
    expect(paths.length).toBeGreaterThan(0);
    const used = new Set<string>();
    const offenders = paths.flatMap((path) => {
      const file = relative(sourceRoot, path).replaceAll('\\', '/');
      return findWholeBodyReads(readFileSync(path, 'utf8'), file).flatMap((finding) => {
        const key = `${file}::${finding.function}`;
        if (allowed[key] !== undefined) {
          used.add(key);
          return [];
        }
        return [`${file}:${finding.line}:${finding.function}`];
      });
    });
    expect(offenders).toEqual([]);
    for (const [key, reason] of Object.entries(allowed)) {
      expect(reason.trim(), key).not.toBe('');
      expect(used.has(key), `stale allow-list entry: ${key}`).toBe(true);
    }
  });
});
