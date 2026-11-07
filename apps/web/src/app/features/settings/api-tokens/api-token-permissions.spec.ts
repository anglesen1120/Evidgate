import catalogue from '../../../../../../../contracts/permissions.json';
import { tokenPermissionChoices } from './api-token-permissions';

describe('API token permission choices', () => {
  it('uses the canonical administrative deny patterns and offers a sorted caller-held subset', () => {
    const codes = new Set(catalogue.permissions.map((row) => row.code));
    const choices = tokenPermissionChoices(codes, () => true);
    expect(choices).toContain('run:read');
    expect(choices).not.toContain('api_token:read');
    expect(choices).not.toContain('api_token:write');
    expect(choices).toEqual([...choices].sort());
    for (const pattern of catalogue.token_denied) {
      expect(
        choices.some((code) =>
          pattern.endsWith(':*') ? code.startsWith(pattern.slice(0, -1)) : code === pattern,
        ),
      ).toBe(false);
    }
  });

  it('rechecks SessionStore.has instead of trusting an old catalogue of grants', () => {
    const codes = new Set(['suite:read', 'run:read', 'membership:write', 'support:read']);
    expect(tokenPermissionChoices(codes, (code) => code !== 'suite:read')).toEqual(['run:read']);
    expect(tokenPermissionChoices(codes, () => false)).toEqual([]);
  });
});
