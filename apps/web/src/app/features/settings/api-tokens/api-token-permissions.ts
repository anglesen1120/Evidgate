// Only the token deny set is shipped: the browser renders permission codes, never the role names or
// descriptions in `contracts/permissions.json`, and importing that whole catalogue put contract
// prose in the lazy chunk (the production fixture-leak gate rejects it). The catalogue stays the
// source: the spec fails when this list and its `token_denied` differ.
export const TOKEN_DENIED_PATTERNS: readonly string[] = [
  'membership:*',
  'role:*',
  'organization:write',
  'project:create',
  'project:write',
  'ci_binding:write',
  'api_token:*',
  'ingest_token:write',
  'service_identity:*',
  'sso:*',
  'support:*',
  'billing:*',
  'healing:approve',
  'healing:revoke',
  'healing:configure',
  'policy:write',
  'artifact:read_sensitive',
  'audit:read',
  'security_event:read',
  'webhook:write',
];

export function tokenPermissionChoices(
  codes: ReadonlySet<string>,
  has: (code: string) => boolean,
): readonly string[] {
  return [...codes]
    .filter(
      (code) =>
        has(code) &&
        !TOKEN_DENIED_PATTERNS.some((denied) =>
          denied.endsWith(':*') ? code.startsWith(denied.slice(0, -1)) : code === denied,
        ),
    )
    .sort();
}
