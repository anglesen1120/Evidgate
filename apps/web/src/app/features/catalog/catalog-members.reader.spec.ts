import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MembershipListResponse } from '@evidgate/sdk';
import { ApiRequestError } from '../../core/http/api-error';
import { MembersDataService } from '../../core/data/members-data.service';
import { CatalogMembersReader } from './catalog-members.reader';
import memberships from '../../../../../../fixtures/web-api/memberships-list/input.json';
import { CATALOG_FAILURE } from './case-list-store.fixtures';

const MEMBERSHIPS = memberships.body as MembershipListResponse;

describe('CatalogMembersReader', () => {
  let listMemberships = vi.fn<MembersDataService['listMemberships']>();
  beforeEach(() => {
    listMemberships = vi.fn<MembersDataService['listMemberships']>().mockResolvedValue(MEMBERSHIPS);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        CatalogMembersReader,
        { provide: MembersDataService, useValue: { listMemberships } },
      ],
    });
  });
  /** The reader is fire-and-forget from `init`; its loop resolves after a few microtasks. */
  async function settle(): Promise<void> {
    for (let turn = 0; turn < 6; turn += 1) await Promise.resolve();
  }

  it('keys every membership by id and offers only active ones as owners', async () => {
    const reader = TestBed.inject(CatalogMembersReader);
    reader.init('org-1');
    await settle();
    expect(reader.status()).toBe('ready');
    expect(reader.members().size).toBe(5);
    expect(reader.members().get('018f0000-0000-7000-8000-0000000000c2')?.invited_email).toBe(
      'qa@acme.test',
    );
    expect(reader.assignable().map((member) => member.invited_email)).toEqual([
      'owner@acme.test',
      'qa@acme.test',
      'release@acme.test',
    ]);
    expect(listMemberships).toHaveBeenCalledWith('org-1', undefined);
  });

  it('reads the same org once and re-reads it only through reload', async () => {
    const reader = TestBed.inject(CatalogMembersReader);
    reader.init('org-1');
    await settle();
    reader.init('org-1');
    await settle();
    expect(listMemberships).toHaveBeenCalledTimes(1);
    reader.reload();
    await settle();
    expect(listMemberships).toHaveBeenCalledTimes(2);
  });

  it('follows the cursor to the last page and merges the rows', async () => {
    const first = MEMBERSHIPS.items[0];
    if (!first) throw new Error('No membership fixture');
    const second = { ...first, id: '018f0000-0000-7000-8000-0000000000c9' };
    listMemberships
      .mockResolvedValueOnce({ items: [first], next_cursor: 'members-page-2' })
      .mockResolvedValueOnce({ items: [second], next_cursor: null });
    const reader = TestBed.inject(CatalogMembersReader);
    reader.init('org-1');
    await settle();
    expect(reader.status()).toBe('ready');
    expect(reader.members().size).toBe(2);
    expect(listMemberships).toHaveBeenLastCalledWith('org-1', 'members-page-2');
  });

  it('reports a typed refusal instead of dropping the owner labels silently', async () => {
    listMemberships.mockRejectedValueOnce(
      new ApiRequestError({ ...CATALOG_FAILURE, kind: 'forbidden', status: 403 }),
    );
    const reader = TestBed.inject(CatalogMembersReader);
    reader.init('org-1');
    await settle();
    expect(reader.status()).toBe('error');
    expect(reader.failure()?.kind).toBe('forbidden');
    expect(reader.members().size).toBe(0);
  });

  it('refuses a repeated cursor rather than looping', async () => {
    listMemberships.mockResolvedValue({ items: [], next_cursor: 'members-page-2' });
    const reader = TestBed.inject(CatalogMembersReader);
    reader.init('org-1');
    await settle();
    expect(reader.status()).toBe('error');
    expect(listMemberships).toHaveBeenCalledTimes(2);
  });
});
