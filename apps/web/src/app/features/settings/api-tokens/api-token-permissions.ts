import catalogue from '../../../../../../../contracts/permissions.json';

export function tokenPermissionChoices(
  codes: ReadonlySet<string>,
  has: (code: string) => boolean,
): readonly string[] {
  return [...codes]
    .filter(
      (code) =>
        has(code) &&
        !catalogue.token_denied.some((denied) =>
          denied.endsWith(':*') ? code.startsWith(denied.slice(0, -1)) : code === denied,
        ),
    )
    .sort();
}
