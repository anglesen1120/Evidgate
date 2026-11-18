import type { HttpRequest } from '@angular/common/http';
import type { CaseListResponse } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog';
import { queryParameter } from './fixture-routes';

/** Model the server's exact filters on a selected specimen, never consume its continuation. */
export function caseListFixtureBody(entry: FixtureEntry, request: HttpRequest<unknown>): unknown {
  if (entry.document.operation_id !== 'listCases' || entry.document.status !== 200)
    return entry.document.body;
  const status = queryParameter(request.urlWithParams, 'status');
  const framework = queryParameter(request.urlWithParams, 'framework');
  const page = entry.document.body as CaseListResponse;
  return {
    ...page,
    items: page.items.filter(
      (row) => (!status || row.status === status) && (!framework || row.framework === framework),
    ),
  };
}
