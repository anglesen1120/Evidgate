import { findRetentionFixture, RETENTION_DEFAULT } from './fixture-retention';
import { findFixtureEntry } from './fixture-catalog';
import {
  findSettingsRetentionWriteFixture,
  SETTINGS_RETENTION_WRITE_VARIANT,
} from './fixture-settings-permissions';
import type { RetentionPolicy } from '@evidgate/sdk';

describe('retention fixture supplement', () => {
  it('returns project defaults and echoes successful writes on subsequent reads', () => {
    const policies = new Map<string, RetentionPolicy>();
    expect(findRetentionFixture('GET', 'ok', 'p-1', null, policies)?.document.body).toEqual(
      RETENTION_DEFAULT,
    );
    const changed = { artifact_days: 7, metadata_days: 60 };
    expect(
      findRetentionFixture('PUT', 'ok', 'p-1', JSON.stringify(changed), policies)?.document.body,
    ).toEqual(changed);
    expect(findRetentionFixture('GET', 'ok', 'p-1', null, policies)?.document.body).toEqual(
      changed,
    );
    expect(findRetentionFixture('GET', 'ok', 'p-2', null, policies)?.document.body).toEqual(
      RETENTION_DEFAULT,
    );
  });

  it.each([
    ['forbidden', 403],
    ['not-found', 404],
    ['conflict', 409],
    ['quota', 429],
    ['unavailable', 503],
  ] as const)('answers the %s refusal without storing the requested values', (variant, status) => {
    const policies = new Map<string, RetentionPolicy>();
    const answer = findRetentionFixture(
      'PUT',
      variant,
      'p-1',
      { artifact_days: 1, metadata_days: 30 },
      policies,
    );
    expect(answer?.document.status).toBe(status);
    expect(policies.size).toBe(0);
    expect(findRetentionFixture('GET', 'ok', 'p-1', null, policies)?.document.status).toBe(200);
  });

  it('leaves unknown variant handling to the fixture backend', () => {
    expect(findRetentionFixture('GET', 'typo', 'p-1', null, new Map())).toBeUndefined();
  });

  it('serves the out-of-range 422 from the corpus, not from the supplement', () => {
    expect(findRetentionFixture('PUT', 'out_of_range', 'p-1', null, new Map())).toBeUndefined();
    expect(findRetentionFixture('PUT', 'out-of-range', 'p-1', null, new Map())).toBeUndefined();
    expect(findFixtureEntry('setRetentionPolicy', 'out_of_range')?.document.status).toBe(422);
  });

  it('adds only project write permission to the corpus settings admin page', () => {
    const admin = findFixtureEntry('listPermissions', 'settings-admin')?.document.body as {
      items: { code: string }[];
    };
    const write = findSettingsRetentionWriteFixture(SETTINGS_RETENTION_WRITE_VARIANT)?.document
      .body as { items: { code: string }[] };
    expect(write.items.map((row) => row.code).sort()).toEqual(
      [...admin.items.map((row) => row.code), 'project:write'].sort(),
    );
    expect(admin.items.some((row) => row.code === 'project:write')).toBe(false);
    expect(write.items.some((row) => row.code === 'project:write')).toBe(true);
  });
});
