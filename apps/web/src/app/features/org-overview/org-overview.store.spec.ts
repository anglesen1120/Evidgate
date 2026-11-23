import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MembershipListResponse, ProjectDto, RunPage, UsageSummaryDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';

import { AuditDataService } from '../../core/data/audit-data.service';
import { MembersDataService } from '../../core/data/members-data.service';
import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';
import { ApiRequestError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { OrgOverviewStore, PROJECT_LIMIT, RUN_LIMIT } from './org-overview.store';

const project: ProjectDto = {
  id: 'p1',
  org_id: 'o1',
  name: 'Shop',
  slug: 'shop',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-11-01T00:00:00Z',
  updated_at: '2026-11-01T00:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'SHOP',
};
const emptyRuns: RunPage = { items: [], next_cursor: null };

describe('OrgOverviewStore', () => {
  const org = signal({ id: 'o1' });
  const grants = signal(
    new Set(['project:read', 'run:read', 'membership:read', 'usage:read', 'audit:read']),
  );
  const projects = { listProjects: vi.fn(), listRuns: vi.fn() };
  const members = { listMemberships: vi.fn() };
  const quota = { summary: vi.fn() };
  const audit = { list: vi.fn() };
  const settle = async (): Promise<void> => {
    TestBed.tick();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(TestBed.inject(OrgOverviewStore).projects.isLoading()).toBe(false);
    });
    await Promise.resolve();
    TestBed.tick();
  };

  beforeEach(() => {
    vi.resetAllMocks();
    org.set({ id: 'o1' });
    grants.set(
      new Set(['project:read', 'run:read', 'membership:read', 'usage:read', 'audit:read']),
    );
    projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
    projects.listRuns.mockResolvedValue(emptyRuns);
    members.listMemberships.mockResolvedValue({
      items: [],
      next_cursor: null,
    } satisfies MembershipListResponse);
    quota.summary.mockResolvedValue({
      period: '2026-11',
      project: null,
      metrics: [],
      next_cursor: null,
    } satisfies UsageSummaryDto);
    audit.list.mockResolvedValue({ items: [], next_cursor: null });
    TestBed.configureTestingModule({
      providers: [
        OrgOverviewStore,
        {
          provide: SessionStore,
          useValue: { currentOrg: org, has: (code: string) => grants().has(code) },
        },
        { provide: OnboardingDataService, useValue: projects },
        { provide: MembersDataService, useValue: members },
        { provide: QuotaDataService, useValue: quota },
        { provide: AuditDataService, useValue: audit },
      ],
    });
  });

  it('bounds all reads and preserves a successful empty membership sample as zero', async () => {
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    expect(projects.listProjects).toHaveBeenCalledWith(
      'o1',
      { limit: PROJECT_LIMIT },
      expect.any(AbortSignal),
    );
    expect(projects.listRuns).toHaveBeenCalledWith(
      'p1',
      { limit: RUN_LIMIT },
      expect.any(AbortSignal),
    );
    expect(members.listMemberships).toHaveBeenCalledWith(
      'o1',
      undefined,
      50,
      expect.any(AbortSignal),
    );
    expect(audit.list).toHaveBeenCalledWith('o1', { limit: 5 }, undefined, expect.any(AbortSignal));
    expect(store.activeMembers()).toBe(0);
    expect(store.pendingInvites()).toBe(0);
    expect(store.aiUsage()).toBeNull();
  });

  it('does not call or expose widgets without their permission', () => {
    grants.set(new Set());
    const store = TestBed.inject(OrgOverviewStore);
    TestBed.tick();
    expect(projects.listProjects).not.toHaveBeenCalled();
    expect(members.listMemberships).not.toHaveBeenCalled();
    expect(quota.summary).not.toHaveBeenCalled();
    expect(audit.list).not.toHaveBeenCalled();
    expect(store.projectRows()).toEqual([]);
  });

  it('retains projects and usage when the independent membership widget fails and retries only it', async () => {
    members.listMemberships.mockRejectedValueOnce(new Error('offline'));
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    expect(store.members.error()).toBeTruthy();
    expect(store.projectRows()).toHaveLength(1);
    expect(store.usage.hasValue()).toBe(true);
    store.members.reload();
    await settle();
    expect(members.listMemberships).toHaveBeenCalledTimes(2);
    expect(projects.listProjects).toHaveBeenCalledTimes(1);
    expect(store.members.error()).toBeUndefined();
  });

  it('never calls an ascending partial run page the latest run', async () => {
    projects.listRuns.mockResolvedValue({ items: [], next_cursor: 'more' });
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.runs.isLoading()).toBe(false);
    });
    expect(store.projectRows()[0]?.runStatus).toBe('unavailable');
    expect(store.projectRows()[0]?.run).toBeNull();
  });

  it('uses the final run only on a complete page and does not infer a missing decision', async () => {
    const run = {
      run_id: 'run1',
      state: 'collecting',
      created_at: '2026-11-23T00:00:00Z',
      ref: {},
      unverified_ref: false,
      completeness: null,
      parent_run_id: null,
      rerun_scope: null,
      decision: null,
    };
    projects.listRuns.mockResolvedValue({ items: [run], next_cursor: null });
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.runs.isLoading()).toBe(false);
    });
    expect(store.projectRows()[0]?.runStatus).toBe('available');
    expect(store.projectRows()[0]?.run?.created_at).toBe(run.created_at);
    expect(store.projectRows()[0]?.run?.decision).toBeNull();
  });

  it('retains project records when one run enrichment fails', async () => {
    projects.listProjects.mockResolvedValue({
      items: [project, { ...project, id: 'p2' }],
      next_cursor: null,
    });
    projects.listRuns.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce(emptyRuns);
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.runs.isLoading()).toBe(false);
    });
    expect(store.projectRows().map((row) => row.runStatus)).toEqual(['error', 'empty']);
    expect(store.projectRows().map((row) => row.project.id)).toEqual(['p1', 'p2']);
  });

  it('clears old scope and permission-protected facts on organization or permission change', async () => {
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    grants.set(new Set());
    org.set({ id: 'o2' });
    TestBed.tick();
    expect(store.projectRows()).toEqual([]);
    expect(store.activeMembers()).toBeNull();
    expect(store.aiUsage()).toBeNull();
  });

  it('classifies forbidden run enrichment as hidden without losing its API error', async () => {
    const failure = new ApiRequestError({ ...mapNetworkError(), kind: 'forbidden', status: 403 });
    projects.listRuns.mockRejectedValue(failure);
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.runs.isLoading()).toBe(false);
    });
    expect(store.projectRows()[0]).toMatchObject({
      project,
      run: null,
      runStatus: 'hidden',
      runError: failure,
    });
  });

  it('marks run enrichment as loading until its pending read resolves', async () => {
    const pending = new Subject<RunPage>();
    projects.listRuns.mockReturnValue(firstValueFrom(pending));
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    expect(store.projectRows()[0]?.runStatus).toBe('loading');
    pending.next(emptyRuns);
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.projectRows()[0]?.runStatus).toBe('empty');
    });
  });

  it('aborts an in-flight run read on organization switch and discards its late result', async () => {
    const pending = new Subject<RunPage>();
    const read = firstValueFrom(pending);
    let signal: AbortSignal | undefined;
    projects.listRuns.mockImplementationOnce((_id, _query, abortSignal: AbortSignal) => {
      signal = abortSignal;
      return read;
    });
    const store = TestBed.inject(OrgOverviewStore);
    await settle();
    expect(signal?.aborted).toBe(false);
    projects.listProjects.mockResolvedValue({
      items: [{ ...project, id: 'p2', org_id: 'o2' }],
      next_cursor: null,
    });
    org.set({ id: 'o2' });
    TestBed.tick();
    await vi.waitFor(() => {
      TestBed.tick();
      expect(signal?.aborted).toBe(true);
    });
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.projectRows()[0]?.project.id).toBe('p2');
      expect(store.projectRows()[0]?.runStatus).toBe('empty');
    });
    pending.next({ items: [], next_cursor: 'old-org' });
    await read;
    TestBed.tick();
    expect(store.projectRows()[0]?.project.id).toBe('p2');
    expect(store.projectRows()[0]?.runStatus).toBe('empty');
  });
});
