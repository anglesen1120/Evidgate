import getAfterGrant from '../../../../fixtures/web-api/membership-get-after-grant/input.json';
import membershipsList from '../../../../fixtures/web-api/memberships-list/input.json';
import rolesList from '../../../../fixtures/web-api/roles-list/input.json';
import type { ShellLocale } from './support';

export const MEMBERSHIPS = membershipsList.body.items;
export const ROLES = rolesList.body.items;
type Role = (typeof ROLES)[number];

function grantTarget() {
  const member = MEMBERSHIPS.find(
    (candidate) =>
      candidate.status === 'active' &&
      !candidate.roles.some((role) => role.code === 'tenant_admin'),
  );
  if (!member) throw new Error('Expected an active non-admin grant target');
  return member;
}

function soleAdmin() {
  const admins = MEMBERSHIPS.filter(
    (candidate) =>
      candidate.status === 'active' && candidate.roles.some((role) => role.code === 'tenant_admin'),
  );
  const [member] = admins;
  if (!member || admins.length !== 1) throw new Error('Expected exactly one active tenant admin');
  return member;
}

function tenantAdminRole() {
  const role = ROLES.find((candidate) => candidate.code === 'tenant_admin');
  if (!role) throw new Error('Expected the built-in tenant admin role');
  return role;
}

export const GRANT_TARGET = grantTarget();
export const SOLE_ADMIN = soleAdmin();
export const TENANT_ADMIN_ROLE = tenantAdminRole();
export const ROLES_AFTER = getAfterGrant.body.roles;

const viLabels: Readonly<Record<string, string>> = {
  viewer: 'Người xem',
  qa_engineer: 'Kỹ sư QA',
  test_maintainer: 'Bảo trì kiểm thử',
  release_approver: 'Duyệt phát hành',
  tenant_admin: 'Quản trị tổ chức',
  ci_producer: 'Bên gửi kết quả CI',
  agent: 'Tác nhân',
  platform_operator: 'Vận hành nền tảng',
  billing_operator: 'Vận hành thanh toán',
};

export function expectedLabel(role: Role, locale: ShellLocale): string {
  if (locale === 'en' || !role.is_builtin) return role.name;
  const label = viLabels[role.code];
  if (!label) throw new Error(`Missing Vietnamese test label for ${role.code}`);
  return label;
}

export function expectedLabels(roles: readonly Role[], locale: ShellLocale): string[] {
  return roles.map((role) => expectedLabel(role, locale)).sort();
}
