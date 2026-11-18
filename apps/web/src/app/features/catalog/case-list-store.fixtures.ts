import type { CaseListResponse } from '@evidgate/sdk';
import catalogCases from '../../../../../../fixtures/web-api/catalog-cases-list/input.json';
import type { ApiError } from '../../core/http/api-error';

export const CASE_LIST_FIXTURE = catalogCases.body as CaseListResponse;
export const CATALOG_FAILURE: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Catalog is unavailable.',
  requestId: 'request-catalog',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};
