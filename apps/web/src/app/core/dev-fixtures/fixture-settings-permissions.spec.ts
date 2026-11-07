// The Settings admin review session, checked for the rule it exists to prove: it is the corpus's
// own `settings-admin` page plus the corpus's own `usage:read` row, in code order, and nothing
// else — so pointing the strip's e2e at it grants one more tab and no other privilege.

import type { PermissionView } from '@evidgate/sdk';

import { findFixtureEntry } from './fixture-catalog';
import {
  findSettingsAdminUsageFixture,
  SETTINGS_ADMIN_USAGE_VARIANT,
  SETTINGS_API_TOKENS_READ_VARIANT,
  SETTINGS_API_TOKENS_WRITE_VARIANT,
} from './fixture-settings-permissions';

function rowsOf(variant: string): readonly PermissionView[] {
  const body = findFixtureEntry('listPermissions', variant)?.document.body as
    { items?: readonly PermissionView[] } | undefined;
  if (body?.items === undefined) throw new Error(`the corpus lost its ${variant} page`);
  return body.items;
}

function specimen(): { items: readonly PermissionView[]; next_cursor: unknown } {
  const entry = findSettingsAdminUsageFixture(SETTINGS_ADMIN_USAGE_VARIANT);
  if (entry?.document.body === undefined) throw new Error('the specimen serves no page');
  return entry.document.body as { items: readonly PermissionView[]; next_cursor: unknown };
}

describe('settings admin+usage permissions specimen', () => {
  it('serves the merged page as the listPermissions document', () => {
    const entry = findSettingsAdminUsageFixture(SETTINGS_ADMIN_USAGE_VARIANT);
    expect(entry?.document.operation_id).toBe('listPermissions');
    expect(entry?.document.variant).toBe(SETTINGS_ADMIN_USAGE_VARIANT);
    expect(entry?.document.status).toBe(200);
    expect(entry?.name).toBe(`permissions-list-${SETTINGS_ADMIN_USAGE_VARIANT}`);
  });

  it('answers every other toggle with nothing, so the corpus answers as before', () => {
    expect(findSettingsAdminUsageFixture('settings-admin')).toBeUndefined();
    expect(findSettingsAdminUsageFixture('')).toBeUndefined();
    expect(findSettingsAdminUsageFixture('settings-admin-usage-extra')).toBeUndefined();
  });

  it('adds the corpus usage:read row and keeps every admin row unchanged', () => {
    const admin = rowsOf('settings-admin');
    const merged = specimen().items;
    expect(merged).toHaveLength(admin.length + 1);

    const usage = rowsOf('usage-read').find((row) => row.code === 'usage:read');
    expect(usage).toBeDefined();
    expect(merged.find((row) => row.code === 'usage:read')).toEqual(usage);

    // Every admin row survives verbatim: removing the added row gives the corpus page back.
    expect(merged.filter((row) => row.code !== 'usage:read')).toEqual(admin);
  });

  it('orders the page by code, as a catalogue page is', () => {
    const codes = specimen().items.map((row) => row.code);
    expect(codes).toEqual([...codes].sort());
    // The one grant is the tab's read, granted to tenants, and it is not a reserved code.
    expect(codes).toContain('usage:read');
    expect(specimen().items.find((row) => row.code === 'usage:read')?.tenant_assignable).toBe(true);
  });

  it('stays a complete page, like the corpus rows it merges', () => {
    expect(specimen().next_cursor).toBeNull();
  });

  it.each([SETTINGS_API_TOKENS_READ_VARIANT, SETTINGS_API_TOKENS_WRITE_VARIANT])(
    'derives API token review grants from the catalogue: %s',
    (variant) => {
      const entry = findSettingsAdminUsageFixture(variant);
      const body = entry?.document.body as { items: PermissionView[] };
      const codes = body.items.map((row) => row.code);
      expect(codes).toContain('api_token:read');
      expect(codes).toContain('run:read');
      expect(codes.includes('api_token:write')).toBe(variant === SETTINGS_API_TOKENS_WRITE_VARIANT);
      expect(new Set(codes).size).toBe(codes.length);
      expect(codes).toEqual([...codes].sort());
      expect(
        body.items
          .filter((row) => row.code.startsWith('api_token:'))
          .every((row) => row.tenant_assignable),
      ).toBe(true);
    },
  );
});
