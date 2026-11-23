import { computed, inject, resource, type Signal } from '@angular/core';
import { signalStore, withProps } from '@ngrx/signals';
import { ProjectDashboardDataService } from '../../../core/data/project-dashboard-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { mapApiError } from '../../../core/http/error-mapper';

export interface DashboardRead<T> {
  readonly visible: Signal<boolean>;
  readonly loading: Signal<boolean>;
  readonly value: Signal<T | undefined>;
  readonly error: Signal<ApiError | null>;
  readonly reload: () => void;
}

function widgetRead<T>(
  scope: () => string | undefined,
  loader: (id: string, signal: AbortSignal) => Promise<T>,
): DashboardRead<T> {
  const read = resource({
    params: scope,
    loader: ({ params, abortSignal }) => loader(params, abortSignal),
  });
  const error = computed(() => {
    const failure = read.error();
    if (failure === undefined) return null;
    return isApiRequestError(failure) ? failure.error : mapApiError({ status: 0, body: null });
  });
  return {
    visible: computed(() => scope() !== undefined && error()?.kind !== 'forbidden'),
    loading: computed(() => read.isLoading()),
    value: computed(() => (scope() !== undefined && read.hasValue() ? read.value() : undefined)),
    error,
    reload: () => {
      read.reload();
    },
  };
}

export const ProjectDashboardStore = signalStore(
  withProps(() => {
    const session = inject(SessionStore);
    const data = inject(ProjectDashboardDataService);
    const projectScope = (permission: string) => () =>
      session.currentOrg() && session.has(permission) ? session.currentProject()?.id : undefined;
    const orgScope = (permission: string) => () =>
      session.currentProject() && session.has(permission) ? session.currentOrg()?.id : undefined;
    return {
      runs: widgetRead(projectScope('run:read'), (id, signal) => data.runs(id, signal)),
      waivers: widgetRead(projectScope('policy:read'), (id, signal) => data.waivers(id, signal)),
      quarantine: widgetRead(projectScope('policy:read'), (id, signal) =>
        data.quarantine(id, signal),
      ),
      incidents: widgetRead(projectScope('healing:triage'), (id, signal) =>
        data.incidents(id, signal),
      ),
      bindings: widgetRead(projectScope('project:read'), (id, signal) => data.bindings(id, signal)),
      tree: widgetRead(projectScope('authoring:read'), (id, signal) => data.tree(id, signal)),
      cases: widgetRead(projectScope('authoring:read'), (id, signal) => data.cases(id, signal)),
      changes: widgetRead(projectScope('authoring:read'), (id, signal) => data.changes(id, signal)),
      usage: widgetRead(orgScope('usage:read'), (id, signal) => data.usage(id, signal)),
      audit: widgetRead(orgScope('audit:read'), (id, signal) => data.audit(id, signal)),
      decisionsVisible: computed(() => session.has('decision:read')),
    };
  }),
);
