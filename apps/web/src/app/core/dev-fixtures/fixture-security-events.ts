import type { HttpRequest } from '@angular/common/http';
import type { SecurityEventList } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog';
import { queryParameter } from './fixture-routes';

export function securityEventsBody(entry: FixtureEntry, request: HttpRequest<unknown>): unknown {
  if (entry.document.operation_id !== 'listSecurityEvents' || entry.document.status !== 200)
    return entry.document.body;
  const page = entry.document.body as SecurityEventList;
  const project = queryParameter(request.urlWithParams, 'project_id');
  const type = queryParameter(request.urlWithParams, 'type');
  const severity = queryParameter(request.urlWithParams, 'severity');
  const from = queryParameter(request.urlWithParams, 'from');
  const to = queryParameter(request.urlWithParams, 'to');
  return {
    ...page,
    items: page.items.filter(
      (row) =>
        (!project || row.project_id === project) &&
        (!type || row.type === type) &&
        (!severity || row.severity === severity) &&
        (!from || Date.parse(row.created_at) >= Date.parse(from)) &&
        (!to || Date.parse(row.created_at) < Date.parse(to)),
    ),
  };
}
