import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

export const SCOPE_ORGANIZATIONS = [
  { id: 'org-one', name: 'Lotus Commerce', slug: 'lotus', status: 'active' },
  {
    id: 'org-two',
    name: 'Tổ chức có tên rất dài để kiểm tra giao diện',
    slug: 'long',
    status: 'active',
  },
] as const satisfies readonly OrganizationSummaryDto[];
const firstProject: ProjectDto = {
  id: 'project-one',
  org_id: 'org-one',
  name: 'storefront-web',
  slug: 'project-one',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-24T00:00:00Z',
  updated_at: '2026-10-24T00:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'SHOP',
};
export const SCOPE_PROJECTS = [
  firstProject,
  {
    ...firstProject,
    id: 'project-two',
    slug: 'project-two',
    name: 'Dự án có tên rất dài để kiểm tra giao diện',
  },
] as const satisfies readonly ProjectDto[];
