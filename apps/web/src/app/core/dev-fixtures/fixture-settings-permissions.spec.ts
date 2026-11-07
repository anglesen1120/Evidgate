// The retention writer session is the corpus's own `settings-admin` page plus one `project:write`
// row, in code order, and nothing else — so the e2e that saves a window gains one grant only.

import type { PermissionView } from '@evidgate/sdk';

import { findFixtureEntry } from './fixture-catalog';
import {
  findSettingsRetentionWriteFixture,
  SETTINGS_RETENTION_WRITE_VARIANT,
} from './fixture-settings-permissions';

function rowsOf(variant: string): readonly PermissionView[] {
  const body = findFixtureEntry('listPermissions', variant)?.document.body as
    { items?: readonly PermissionView[] } | undefined;
  if (body?.items === undefined) throw new Error(`the corpus lost its ${variant} page`);
  return body.items;
}

function specimen(): { items: readonly PermissionView[]; next_cursor: unknown } {
  const entry = findSettingsRetentionWriteFixture(SETTINGS_RETENTION_WRITE_VARIANT);
  if (entry?.document.body === undefined) throw new Error('the specimen serves no page');
  return entry.document.body as { items: readonly PermissionView[]; next_cursor: unknown };
}

describe('settings retention-write permissions specimen', () => {
  it('serves the merged page as the listPermissions document', () => {
    const entry = findSettingsRetentionWriteFixture(SETTINGS_RETENTION_WRITE_VARIANT);
    expect(entry?.document.operation_id).toBe('listPermissions');
    expect(entry?.document.variant).toBe(SETTINGS_RETENTION_WRITE_VARIANT);
    expect(entry?.document.status).toBe(200);
    expect(entry?.name).toBe(`permissions-list-${SETTINGS_RETENTION_WRITE_VARIANT}`);
  });

  it('answers every other toggle with nothing, so the corpus answers as before', () => {
    expect(findSettingsRetentionWriteFixture('settings-admin')).toBeUndefined();
    expect(findSettingsRetentionWriteFixture('settings-admin-usage')).toBeUndefined();
    expect(findSettingsRetentionWriteFixture('')).toBeUndefined();
  });

  it('adds only project:write to the corpus admin page, which already holds usage:read', () => {
    const admin = rowsOf('settings-admin');
    const merged = specimen().items;
    expect(admin.some((row) => row.code === 'usage:read')).toBe(true);
    expect(merged).toHaveLength(admin.length + 1);
    expect(merged.filter((row) => row.code !== 'project:write')).toEqual(admin);
  });

  it('orders the page by code and stays a complete page', () => {
    const codes = specimen().items.map((row) => row.code);
    expect(codes).toEqual([...codes].sort());
    expect(specimen().next_cursor).toBeNull();
  });
});
