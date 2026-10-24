// 09.1.1-T4.1 (plan 09.1.1 §10 T4, 00 §5.6 W5; D-S68-Q1): the identity reads the app shell needs
// before a route can be matched — the server-declared permission catalogue, the organizations the
// caller belongs to and the projects of one organization. It follows the data-service pattern of
// `runs-data.service.ts`: the generated `@evidgate/sdk` operation is called with the app's one
// client, `ApiGateway.call` decides success versus typed failure, and no wire type is declared
// here — every signature is an SDK type.
//
// Two of the three operations are 00 §9.1 cursor pages. The shell cannot render navigation from a
// first page alone, so the service follows `next_cursor` to the last page; the loop is bounded
// because a shell that keeps paging is worse than a shell that reports the failure, and a server
// that repeats a cursor would otherwise hang the router guard forever.

import { inject, Injectable } from '@angular/core';
import {
  listMyOrganizations,
  listPermissions,
  listProjects,
  type OrganizationSummaryDto,
  type PermissionView,
  type ProjectDto,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/**
 * How many cursor pages one read may follow before it is treated as a contract violation. A page
 * holds the server's default page size (tens of rows), so twenty pages is far above any catalogue,
 * organization or project list the product can hold; reaching it means the cursor loop is broken.
 */
export const MAX_CURSOR_PAGES = 20;

@Injectable({ providedIn: 'root' })
export class IdentityDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * The whole permission catalogue (`GET /v1/permissions`, 02.2.1), every page.
   *
   * This is not the caller's grants: no V1 endpoint returns those (D-S68-Q1). The shell's
   * permission source builds its convenience set from this catalogue; the API's own `403` remains
   * the only real gate.
   */
  listPermissions(): Promise<readonly PermissionView[]> {
    return this.readAllPages<PermissionView>('the permission catalogue', async (cursor) => {
      const page = await this.gateway.call((client) =>
        listPermissions({ client, query: cursor === undefined ? {} : { cursor } }),
      );
      return [page.items, page.next_cursor];
    });
  }

  /**
   * The organizations the caller belongs to (`GET /v1/orgs`). The response is bounded by the
   * server, so this operation carries no cursor: one call is the whole list.
   */
  async listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    const response = await this.gateway.call((client) => listMyOrganizations({ client }));
    return response.items;
  }

  /** Every project of one organization (`GET /v1/orgs/{o}/projects`, 00 §9.1 cursor page). */
  listProjects(orgId: string): Promise<readonly ProjectDto[]> {
    return this.readAllPages<ProjectDto>('the project list', async (cursor) => {
      const page = await this.gateway.call((client) =>
        listProjects({ client, path: { o: orgId }, query: cursor === undefined ? {} : { cursor } }),
      );
      return [page.items, page.next_cursor];
    });
  }

  /**
   * Follows a 00 §9.1 keyset cursor to the last page. `fetchPage` answers one page as
   * `[items, nextCursor]`, and a `null` cursor ends the read.
   *
   * The loop is bounded by {@link MAX_CURSOR_PAGES}, and a cursor the server already handed out is
   * a contract violation rather than a second page: both throw a plain `Error`, which the session
   * store reports as `unavailable` (the read did not complete) instead of pretending a partial list
   * is the whole one.
   */
  private async readAllPages<TItem>(
    label: string,
    fetchPage: (cursor: string | undefined) => Promise<readonly [readonly TItem[], string | null]>,
  ): Promise<readonly TItem[]> {
    const items: TItem[] = [];
    const requested = new Set<string>();
    let cursor: string | undefined;
    for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
      const [pageItems, nextCursor] = await fetchPage(cursor);
      items.push(...pageItems);
      if (nextCursor === null) {
        return items;
      }
      if (requested.has(nextCursor)) {
        throw new Error(`${label} repeated cursor "${nextCursor}"`);
      }
      requested.add(nextCursor);
      cursor = nextCursor;
    }
    throw new Error(`${label} did not finish within ${MAX_CURSOR_PAGES} pages`);
  }
}
