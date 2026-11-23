import { computed, inject, resource } from '@angular/core';
import type { ProjectDto, RunSummary } from '@evidgate/sdk';
import { signalStore, withProps } from '@ngrx/signals';

import { AuditDataService } from '../../core/data/audit-data.service';
import { MembersDataService } from '../../core/data/members-data.service';
import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';

export const PROJECT_LIMIT = 8;
export const MEMBERSHIP_LIMIT = 50;
export const RUN_LIMIT = 50;
export const ACTIVITY_LIMIT = 5;

export interface DashboardProject {
  readonly project: ProjectDto;
  readonly run: RunSummary | null;
  readonly runStatus: 'available' | 'empty' | 'unavailable' | 'error' | 'hidden';
}

export const OrgOverviewStore = signalStore(
  withProps(() => {
    const session = inject(SessionStore);
    const projectsData = inject(OnboardingDataService);
    const membersData = inject(MembersDataService);
    const quotaData = inject(QuotaDataService);
    const auditData = inject(AuditDataService);
    const org = computed(() => session.currentOrg()?.id);
    const canProjects = computed(() => session.has('project:read'));
    const canRuns = computed(() => session.has('run:read'));
    const canMembers = computed(() => session.has('membership:read'));
    const canUsage = computed(() => session.has('usage:read'));
    const canAudit = computed(() => session.has('audit:read'));
    const projects = resource({
      params: () => (canProjects() ? org() : undefined),
      loader: async ({ params }) => {
        const page = await projectsData.listProjects(params, { limit: PROJECT_LIMIT });
        return { items: page.items.slice(0, PROJECT_LIMIT), more: page.next_cursor !== null };
      },
    });
    const runs = resource({
      params: () => {
        const page = projects.hasValue() ? projects.value() : undefined;
        return canRuns() && org() && page ? { org: org(), projects: page.items } : undefined;
      },
      loader: async ({ params }) =>
        Promise.all(
          params.projects.map(async (project): Promise<DashboardProject> => {
            try {
              const page = await projectsData.listRuns(project.id, { limit: RUN_LIMIT });
              // Ascending pages only prove the latest run when the bounded read reaches the end.
              const run = page.next_cursor === null ? (page.items.at(-1) ?? null) : null;
              return {
                project,
                run,
                runStatus: page.next_cursor ? 'unavailable' : run ? 'available' : 'empty',
              };
            } catch {
              return { project, run: null, runStatus: 'error' };
            }
          }),
        ),
    });
    const members = resource({
      params: () => (canMembers() ? org() : undefined),
      loader: ({ params }) => membersData.listMemberships(params, undefined, MEMBERSHIP_LIMIT),
    });
    const usage = resource({
      params: () => (canUsage() ? org() : undefined),
      loader: ({ params, abortSignal }) => quotaData.summary(params, abortSignal, 1),
    });
    const activity = resource({
      params: () => (canAudit() ? org() : undefined),
      loader: ({ params }) => auditData.list(params, { limit: ACTIVITY_LIMIT }),
    });
    const projectRows = computed<readonly DashboardProject[]>(() => {
      if (!projects.hasValue()) return [];
      if (runs.hasValue()) return runs.value();
      return projects.value().items.map((project) => ({
        project,
        run: null,
        runStatus: canRuns() ? 'unavailable' : 'hidden',
      }));
    });
    const activeMembers = computed(() =>
      members.hasValue()
        ? members.value().items.filter((row) => row.status === 'active').length
        : null,
    );
    const pendingInvites = computed(() =>
      members.hasValue()
        ? members.value().items.filter((row) => row.status === 'pending').length
        : null,
    );
    const aiUsage = computed(() =>
      usage.hasValue()
        ? (usage.value().metrics.find((metric) => metric.metric === 'ai_tokens') ?? null)
        : null,
    );
    return {
      session,
      org,
      canProjects,
      canRuns,
      canMembers,
      canUsage,
      canAudit,
      projects,
      runs,
      members,
      usage,
      activity,
      projectRows,
      activeMembers,
      pendingInvites,
      aiUsage,
    };
  }),
);
