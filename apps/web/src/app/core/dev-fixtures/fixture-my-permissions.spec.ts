import permissionsCatalogue from '../../../../../../contracts/permissions.json';
import { findFixtureEntry } from './fixture-catalog';
import { findMyPermissionsFixture } from './fixture-my-permissions';

const lookup = (variant: string) => findFixtureEntry('listPermissions', variant);

function codes(variant: string): readonly string[] {
  const body = findMyPermissionsFixture(variant, 'o-1', null, lookup)?.document.body as
    { permissions: string[] } | undefined;
  return body?.permissions ?? [];
}

describe('getMyPermissions fixture', () => {
  it('serves a limited role as a strict, sorted subset of the catalogue', () => {
    const catalogue = permissionsCatalogue.permissions
      .filter((entry) => entry.tenant_assignable)
      .map((entry) => entry.code);
    const limited = codes('limited');

    expect(limited).toEqual([
      'audit:read',
      'authoring:read',
      'membership:read',
      'project:read',
      'role:read',
    ]);
    expect(limited.length).toBeLessThan(catalogue.length);
    expect(limited.every((code) => catalogue.includes(code))).toBe(true);
    expect([...limited].sort()).toEqual(limited);
  });

  it('never grants a code a member cannot hold, even if the session page lists it', () => {
    expect(codes('ok')).not.toContain('billing:read');
    expect(codes('ok')).not.toContain('billing:write');
  });

  it('echoes the tenant and project it was asked for', () => {
    const body = findMyPermissionsFixture('ok', 'o-9', 'p-9', lookup)?.document.body;

    expect(body).toMatchObject({ org_id: 'o-9', project_id: 'p-9' });
  });

  it('answers nothing for a session with no grant page', () => {
    expect(findMyPermissionsFixture('no-such-session', 'o-1', null, lookup)).toBeUndefined();
  });
});
