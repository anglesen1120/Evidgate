import type { RoleView } from '@evidgate/sdk';

export function memberRoleLabel(role: RoleView): string {
  if (!role.is_builtin) return role.name;
  switch (role.code) {
    case 'viewer':
      return $localize`:@@settings.members.role-viewer:Viewer`;
    case 'qa_engineer':
      return $localize`:@@settings.members.role-qa-engineer:QA engineer`;
    case 'test_maintainer':
      return $localize`:@@settings.members.role-test-maintainer:Test maintainer`;
    case 'release_approver':
      return $localize`:@@settings.members.role-release-approver:Release approver`;
    case 'tenant_admin':
      return $localize`:@@settings.members.role-tenant-admin:Tenant admin`;
    case 'ci_producer':
      return $localize`:@@settings.members.role-ci-producer:CI producer`;
    case 'agent':
      return $localize`:@@settings.members.role-agent:Agent`;
    case 'platform_operator':
      return $localize`:@@settings.members.role-platform-operator:Platform operator`;
    case 'billing_operator':
      return $localize`:@@settings.members.role-billing-operator:Billing operator`;
    default:
      return role.name;
  }
}
