// Reads the keys of a top-level `const <name>: Record<string, true> = { ... }` through the TypeScript
// parser, so layout (one-line literals, comments, trailing commas) cannot change what is read. It
// throws on anything it does not understand: a key set the pin cannot read must fail the pin, not shrink it.
import ts from 'typescript';

function keyName(name: ts.PropertyName): string | undefined {
  if (
    ts.isIdentifier(name) ||
    ts.isStringLiteral(name) ||
    ts.isNoSubstitutionTemplateLiteral(name)
  ) {
    return name.text;
  }
  return undefined;
}

function isRecordOfTrue(type: ts.TypeNode | undefined): boolean {
  return (
    type !== undefined &&
    ts.isTypeReferenceNode(type) &&
    type.typeName.getText() === 'Record' &&
    type.typeArguments?.length === 2 &&
    type.typeArguments[0]?.kind === ts.SyntaxKind.StringKeyword &&
    type.typeArguments[1]?.getText() === 'true'
  );
}

export function readKeySet(sourceText: string, constName: string, file = 'source.ts'): string[] {
  const sf = ts.createSourceFile(file, sourceText, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  for (const statement of sf.statements) {
    if (!ts.isVariableStatement(statement)) continue;
    for (const d of statement.declarationList.declarations) {
      if (!ts.isIdentifier(d.name) || d.name.text !== constName) continue;
      if (
        !isRecordOfTrue(d.type) ||
        d.initializer === undefined ||
        !ts.isObjectLiteralExpression(d.initializer)
      ) {
        throw new Error(
          `${constName} is not \`Record<string, true> = { ... }\`: update this pin test`,
        );
      }
      const keys = d.initializer.properties.map((p) => {
        const name = ts.isPropertyAssignment(p) ? keyName(p.name) : undefined;
        if (
          !ts.isPropertyAssignment(p) ||
          name === undefined ||
          p.initializer.kind !== ts.SyntaxKind.TrueKeyword
        ) {
          throw new Error(`${constName}: cannot read entry ${JSON.stringify(p.getText())}`);
        }
        return name;
      });
      if (keys.length === 0) {
        throw new Error(`${constName} parsed to an empty set`);
      }
      return keys;
    }
  }
  throw new Error(
    `const ${constName}: Record<string, true> = { ... } not found: update this pin test`,
  );
}
