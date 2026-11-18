import { findCatalogOwnerPermissionsFixture } from './fixture-catalog-owner-permissions';

describe('catalog owner permissions fixture', () => {
  it('answers the default grant set plus membership:read, sorted by code', () => {
    const entry = findCatalogOwnerPermissionsFixture('catalog-owner');
    expect(entry?.document.operation_id).toBe('listPermissions');
    expect(entry?.document.status).toBe(200);
    const body = entry?.document.body as { items: readonly { code: string }[] };
    const codes = body.items.map((row) => row.code);
    expect(codes).toContain('membership:read');
    expect(codes).toContain('case:write');
    expect([...codes].sort()).toEqual(codes);
  });

  it('leaves every other variant to the corpus', () => {
    expect(findCatalogOwnerPermissionsFixture('ok')).toBeUndefined();
    expect(findCatalogOwnerPermissionsFixture('settings-admin')).toBeUndefined();
  });
});
