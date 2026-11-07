// The sessions and registrations half of the catalog: shell grants, onboarding seeds, CI
// bindings, ingest tokens, memberships, permissions, policy, project and retention reads. Rows
// keep the catalog order, so the concatenation in `fixture-catalog-entries.ts` serves the same list.

import apiTokenCreate from '../../../../../../fixtures/web-api/api-token-create/input.json';
import apiTokensList from '../../../../../../fixtures/web-api/api-tokens-list/input.json';
import ciBindingCreate from '../../../../../../fixtures/web-api/ci-binding-create/input.json';
import ciBindingCreateConflict from '../../../../../../fixtures/web-api/ci-binding-create-conflict/input.json';
import ciBindingCreateValidation from '../../../../../../fixtures/web-api/ci-binding-create-validation/input.json';
import auditList from '../../../../../../fixtures/web-api/audit-list/input.json';
import auditListEmpty from '../../../../../../fixtures/web-api/audit-list-empty/input.json';
import auditListSupport from '../../../../../../fixtures/web-api/audit-list-support/input.json';
import ciBindingsList from '../../../../../../fixtures/web-api/ci-bindings-list/input.json';
import ciBindingsListOnboardingConnected from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-connected/input.json';
import ciBindingsListOnboardingProject from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-project/input.json';
import ciBindingsListOnboardingRun from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-run/input.json';
import ciBindingsListSettings from '../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import ciBindingsListSettingsEmpty from '../../../../../../fixtures/web-api/ci-bindings-list-settings-empty/input.json';
import ciBindingsListSettingsForbidden from '../../../../../../fixtures/web-api/ci-bindings-list-settings-forbidden/input.json';
import ciBindingsListSettingsPage1 from '../../../../../../fixtures/web-api/ci-bindings-list-settings-page-1/input.json';
import ciBindingsListSettingsPage2 from '../../../../../../fixtures/web-api/ci-bindings-list-settings-page-2/input.json';
import ingestTokenCreate from '../../../../../../fixtures/web-api/ingest-token-create/input.json';
import ingestTokenCreateNameInvalid from '../../../../../../fixtures/web-api/ingest-token-create-name-invalid/input.json';
import ingestTokenRotate from '../../../../../../fixtures/web-api/ingest-token-rotate/input.json';
import ingestTokenRotateConflict from '../../../../../../fixtures/web-api/ingest-token-rotate-conflict/input.json';
import ingestTokenRotateRateLimited from '../../../../../../fixtures/web-api/ingest-token-rotate-rate-limited/input.json';
import ingestTokensList from '../../../../../../fixtures/web-api/ingest-tokens-list/input.json';
import ingestTokensListOnboardingConnected from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-connected/input.json';
import ingestTokensListOnboardingForbidden from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-forbidden/input.json';
import ingestTokensListOnboardingProject from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-project/input.json';
import ingestTokensListOnboardingProjectNotFound from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-project-not-found/input.json';
import ingestTokensListOnboardingRun from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-run/input.json';
import ingestTokensListOnboardingToken from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import ingestTokensListOnboardingTokenUsed from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token-used/input.json';
import ingestTokensListOnboardingUnusable from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-unusable/input.json';
import ingestTokensListSettings from '../../../../../../fixtures/web-api/ingest-tokens-list-settings/input.json';
import membershipCancelNotPending from '../../../../../../fixtures/web-api/membership-cancel-not-pending/input.json';
import membershipCancelOk from '../../../../../../fixtures/web-api/membership-cancel-ok/input.json';
import membershipCreateInviteExists from '../../../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipCreateOk from '../../../../../../fixtures/web-api/membership-create-ok/input.json';
import membershipCreateReplay from '../../../../../../fixtures/web-api/membership-create-replay/input.json';
import membershipGetAfterGrant from '../../../../../../fixtures/web-api/membership-get-after-grant/input.json';
import membershipRemoveInvalidTransition from '../../../../../../fixtures/web-api/membership-remove-invalid-transition/input.json';
import membershipRemoveLastAdmin from '../../../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import membershipRemoveOk from '../../../../../../fixtures/web-api/membership-remove-ok/input.json';
import membershipResendNotPending from '../../../../../../fixtures/web-api/membership-resend-not-pending/input.json';
import membershipResendOk from '../../../../../../fixtures/web-api/membership-resend-ok/input.json';
import membershipResendReplay from '../../../../../../fixtures/web-api/membership-resend-replay/input.json';
import membershipRoleGrantForbidden from '../../../../../../fixtures/web-api/membership-role-grant-forbidden/input.json';
import membershipRoleGrantOk from '../../../../../../fixtures/web-api/membership-role-grant-ok/input.json';
import membershipRoleGrantUnknownRole from '../../../../../../fixtures/web-api/membership-role-grant-unknown-role/input.json';
import membershipRoleRevokeLastAdmin from '../../../../../../fixtures/web-api/membership-role-revoke-last-admin/input.json';
import membershipRoleRevokeOk from '../../../../../../fixtures/web-api/membership-role-revoke-ok/input.json';
import membershipsList from '../../../../../../fixtures/web-api/memberships-list/input.json';
import membershipsListEmpty from '../../../../../../fixtures/web-api/memberships-list-empty/input.json';
import orgsList from '../../../../../../fixtures/web-api/orgs-list/input.json';
import orgsListUnauthenticated from '../../../../../../fixtures/web-api/orgs-list-unauthenticated/input.json';
import orgsListUnavailable from '../../../../../../fixtures/web-api/orgs-list-unavailable/input.json';
import permissionsList from '../../../../../../fixtures/web-api/permissions-list/input.json';
import permissionsListLimited from '../../../../../../fixtures/web-api/permissions-list-limited/input.json';
import permissionsListOnboarding from '../../../../../../fixtures/web-api/permissions-list-onboarding/input.json';
import permissionsListOnboardingEnable from '../../../../../../fixtures/web-api/permissions-list-onboarding-enable/input.json';
import permissionsListOnboardingNoWrites from '../../../../../../fixtures/web-api/permissions-list-onboarding-no-writes/input.json';
import permissionsListSettingsAdmin from '../../../../../../fixtures/web-api/permissions-list-settings-admin/input.json';
import permissionsListSettingsAuditOnly from '../../../../../../fixtures/web-api/permissions-list-settings-audit-only/input.json';
import permissionsListSettingsMembersReadonly from '../../../../../../fixtures/web-api/permissions-list-settings-members-readonly/input.json';
import permissionsListSettingsNoTab from '../../../../../../fixtures/web-api/permissions-list-settings-no-tab/input.json';
import permissionsListSettingsPolicyReadonly from '../../../../../../fixtures/web-api/permissions-list-settings-policy-readonly/input.json';
import permissionsListSettingsPolicyWrite from '../../../../../../fixtures/web-api/permissions-list-settings-policy-write/input.json';
import permissionsListSettingsSecretsWrite from '../../../../../../fixtures/web-api/permissions-list-settings-secrets-write/input.json';
import permissionsListUsageRead from '../../../../../../fixtures/web-api/permissions-list-usage-read/input.json';
import policyActivationForbidden from '../../../../../../fixtures/web-api/policy-activation-forbidden/input.json';
import policyActivationInvalidTransition from '../../../../../../fixtures/web-api/policy-activation-invalid-transition/input.json';
import policyActivationOk from '../../../../../../fixtures/web-api/policy-activation-ok/input.json';
import policyActivationReadEnforcing from '../../../../../../fixtures/web-api/policy-activation-read-enforcing/input.json';
import policyActivationReadForbidden from '../../../../../../fixtures/web-api/policy-activation-read-forbidden/input.json';
import policyActivationReadNotFound from '../../../../../../fixtures/web-api/policy-activation-read-not-found/input.json';
import policyActivationReadOk from '../../../../../../fixtures/web-api/policy-activation-read-ok/input.json';
import policyActivationReadRateLimited from '../../../../../../fixtures/web-api/policy-activation-read-rate-limited/input.json';
import policyActivationUnchanged from '../../../../../../fixtures/web-api/policy-activation-unchanged/input.json';
import policyHeadReadNotFound from '../../../../../../fixtures/web-api/policy-head-read-not-found/input.json';
import policyHeadReadOk from '../../../../../../fixtures/web-api/policy-head-read-ok/input.json';
import policyRevisionCreate from '../../../../../../fixtures/web-api/policy-revision-create/input.json';
import policyRevisionCreateCriticalCaseQuarantined from '../../../../../../fixtures/web-api/policy-revision-create-critical-case-quarantined/input.json';
import policyRevisionCreatePreconditionFailed from '../../../../../../fixtures/web-api/policy-revision-create-precondition-failed/input.json';
import policyRevisionCreateUnchanged from '../../../../../../fixtures/web-api/policy-revision-create-unchanged/input.json';
import policyRevisionCreateUnknownField from '../../../../../../fixtures/web-api/policy-revision-create-unknown-field/input.json';
import policyRevisionCreateVersionPointer from '../../../../../../fixtures/web-api/policy-revision-create-version-pointer/input.json';
import policyRevisionFirst from '../../../../../../fixtures/web-api/policy-revision-first/input.json';
import policyRevisionHead from '../../../../../../fixtures/web-api/policy-revision-head/input.json';
import policyRevisionPrevious from '../../../../../../fixtures/web-api/policy-revision-previous/input.json';
import policyRevisionPublished from '../../../../../../fixtures/web-api/policy-revision-published/input.json';
import policyRevisionsList from '../../../../../../fixtures/web-api/policy-revisions-list/input.json';
import policyRevisionsListAfterPublish from '../../../../../../fixtures/web-api/policy-revisions-list-after-publish/input.json';
import policyRevisionsListConflictHead from '../../../../../../fixtures/web-api/policy-revisions-list-conflict-head/input.json';
import projectCreate from '../../../../../../fixtures/web-api/project-create/input.json';
import projectCreateNameTaken from '../../../../../../fixtures/web-api/project-create-name-taken/input.json';
import projectDetail from '../../../../../../fixtures/web-api/project-detail/input.json';
import projectDetailOnboardingConnected from '../../../../../../fixtures/web-api/project-detail-onboarding-connected/input.json';
import projectDetailOnboardingEmpty from '../../../../../../fixtures/web-api/project-detail-onboarding-empty/input.json';
import projectDetailOnboardingForbidden from '../../../../../../fixtures/web-api/project-detail-onboarding-forbidden/input.json';
import projectDetailOnboardingProjectNotFound from '../../../../../../fixtures/web-api/project-detail-onboarding-project-not-found/input.json';
import projectDetailOnboardingProject from '../../../../../../fixtures/web-api/project-detail-onboarding-project/input.json';
import projectDetailOnboardingRun from '../../../../../../fixtures/web-api/project-detail-onboarding-run/input.json';
import projectUpdatePreconditionFailed from '../../../../../../fixtures/web-api/project-update-precondition-failed/input.json';
import retentionPutOutOfRange from '../../../../../../fixtures/web-api/retention-put-out-of-range/input.json';
import retentionPutOk from '../../../../../../fixtures/web-api/retention-put-ok/input.json';
import retentionReadForbidden from '../../../../../../fixtures/web-api/retention-read-forbidden/input.json';
import retentionReadNotFound from '../../../../../../fixtures/web-api/retention-read-not-found/input.json';
import retentionReadOk from '../../../../../../fixtures/web-api/retention-read-ok/input.json';
import projectsList from '../../../../../../fixtures/web-api/projects-list/input.json';
import projectsListOnboardingEmpty from '../../../../../../fixtures/web-api/projects-list-onboarding-empty/input.json';
import projectsListOnboardingForbidden from '../../../../../../fixtures/web-api/projects-list-onboarding-forbidden/input.json';
import projectsListUnauthenticated from '../../../../../../fixtures/web-api/projects-list-unauthenticated/input.json';
import projectsListOnboardingProjectNotFound from '../../../../../../fixtures/web-api/projects-list-onboarding-project-not-found/input.json';
import rolesList from '../../../../../../fixtures/web-api/roles-list/input.json';

import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_SESSION_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'ci-binding-create', document: ciBindingCreate },
  { name: 'ci-binding-create-conflict', document: ciBindingCreateConflict },
  { name: 'ci-binding-create-validation', document: ciBindingCreateValidation },
  { name: 'audit-list', document: auditList },
  { name: 'audit-list-empty', document: auditListEmpty },
  { name: 'audit-list-support', document: auditListSupport },
  { name: 'ci-bindings-list', document: ciBindingsList },
  { name: 'ci-bindings-list-onboarding-connected', document: ciBindingsListOnboardingConnected },
  { name: 'ci-bindings-list-onboarding-project', document: ciBindingsListOnboardingProject },
  { name: 'ci-bindings-list-onboarding-run', document: ciBindingsListOnboardingRun },
  { name: 'ci-bindings-list-settings', document: ciBindingsListSettings },
  { name: 'ci-bindings-list-settings-empty', document: ciBindingsListSettingsEmpty },
  { name: 'ci-bindings-list-settings-forbidden', document: ciBindingsListSettingsForbidden },
  { name: 'ci-bindings-list-settings-page-1', document: ciBindingsListSettingsPage1 },
  {
    name: 'ci-bindings-list-settings-page-2',
    document: ciBindingsListSettingsPage2,
    cursor: '08df2cdfd69f6800-018f0000-0000-7000-8000-000000000063',
  },
  { name: 'api-token-create', document: apiTokenCreate },
  { name: 'api-tokens-list', document: apiTokensList },
  { name: 'ingest-token-create', document: ingestTokenCreate },
  { name: 'ingest-token-create-name-invalid', document: ingestTokenCreateNameInvalid },
  { name: 'ingest-token-rotate', document: ingestTokenRotate },
  { name: 'ingest-token-rotate-conflict', document: ingestTokenRotateConflict },
  { name: 'ingest-token-rotate-rate-limited', document: ingestTokenRotateRateLimited },
  { name: 'ingest-tokens-list', document: ingestTokensList },
  {
    name: 'ingest-tokens-list-onboarding-connected',
    document: ingestTokensListOnboardingConnected,
  },
  {
    name: 'ingest-tokens-list-onboarding-forbidden',
    document: ingestTokensListOnboardingForbidden,
  },
  { name: 'ingest-tokens-list-onboarding-project', document: ingestTokensListOnboardingProject },
  {
    name: 'ingest-tokens-list-onboarding-project-not-found',
    document: ingestTokensListOnboardingProjectNotFound,
  },
  { name: 'ingest-tokens-list-onboarding-run', document: ingestTokensListOnboardingRun },
  { name: 'ingest-tokens-list-onboarding-token', document: ingestTokensListOnboardingToken },
  {
    name: 'ingest-tokens-list-onboarding-token-used',
    document: ingestTokensListOnboardingTokenUsed,
  },
  {
    name: 'ingest-tokens-list-onboarding-unusable',
    document: ingestTokensListOnboardingUnusable,
  },
  { name: 'ingest-tokens-list-settings', document: ingestTokensListSettings },
  { name: 'membership-cancel-not-pending', document: membershipCancelNotPending },
  { name: 'membership-cancel-ok', document: membershipCancelOk },
  { name: 'membership-create-invite-exists', document: membershipCreateInviteExists },
  { name: 'membership-create-ok', document: membershipCreateOk },
  { name: 'membership-create-replay', document: membershipCreateReplay },
  { name: 'membership-get-after-grant', document: membershipGetAfterGrant },
  { name: 'membership-remove-invalid-transition', document: membershipRemoveInvalidTransition },
  { name: 'membership-remove-last-admin', document: membershipRemoveLastAdmin },
  { name: 'membership-remove-ok', document: membershipRemoveOk },
  { name: 'membership-resend-not-pending', document: membershipResendNotPending },
  { name: 'membership-resend-ok', document: membershipResendOk },
  { name: 'membership-resend-replay', document: membershipResendReplay },
  { name: 'membership-role-grant-forbidden', document: membershipRoleGrantForbidden },
  { name: 'membership-role-grant-ok', document: membershipRoleGrantOk },
  { name: 'membership-role-grant-unknown-role', document: membershipRoleGrantUnknownRole },
  { name: 'membership-role-revoke-last-admin', document: membershipRoleRevokeLastAdmin },
  { name: 'membership-role-revoke-ok', document: membershipRoleRevokeOk },
  { name: 'memberships-list', document: membershipsList },
  { name: 'memberships-list-empty', document: membershipsListEmpty },
  { name: 'orgs-list', document: orgsList },
  { name: 'orgs-list-unauthenticated', document: orgsListUnauthenticated },
  { name: 'orgs-list-unavailable', document: orgsListUnavailable },
  { name: 'permissions-list', document: permissionsList },
  { name: 'permissions-list-limited', document: permissionsListLimited },
  { name: 'permissions-list-onboarding', document: permissionsListOnboarding },
  { name: 'permissions-list-onboarding-enable', document: permissionsListOnboardingEnable },
  {
    name: 'permissions-list-onboarding-no-writes',
    document: permissionsListOnboardingNoWrites,
  },
  { name: 'permissions-list-settings-admin', document: permissionsListSettingsAdmin },
  { name: 'permissions-list-settings-audit-only', document: permissionsListSettingsAuditOnly },
  {
    name: 'permissions-list-settings-members-readonly',
    document: permissionsListSettingsMembersReadonly,
  },
  { name: 'permissions-list-settings-no-tab', document: permissionsListSettingsNoTab },
  {
    name: 'permissions-list-settings-policy-readonly',
    document: permissionsListSettingsPolicyReadonly,
  },
  {
    name: 'permissions-list-settings-policy-write',
    document: permissionsListSettingsPolicyWrite,
  },
  {
    name: 'permissions-list-settings-secrets-write',
    document: permissionsListSettingsSecretsWrite,
  },
  { name: 'permissions-list-usage-read', document: permissionsListUsageRead },
  { name: 'policy-activation-forbidden', document: policyActivationForbidden },
  { name: 'policy-activation-invalid-transition', document: policyActivationInvalidTransition },
  { name: 'policy-activation-ok', document: policyActivationOk },
  { name: 'policy-activation-read-enforcing', document: policyActivationReadEnforcing },
  { name: 'policy-activation-read-forbidden', document: policyActivationReadForbidden },
  { name: 'policy-activation-read-not-found', document: policyActivationReadNotFound },
  { name: 'policy-activation-read-ok', document: policyActivationReadOk },
  {
    name: 'policy-activation-read-rate-limited',
    document: policyActivationReadRateLimited,
  },
  { name: 'policy-activation-unchanged', document: policyActivationUnchanged },
  { name: 'policy-head-read-ok', document: policyHeadReadOk },
  { name: 'policy-head-read-not-found', document: policyHeadReadNotFound },
  { name: 'policy-revision-create', document: policyRevisionCreate },
  {
    name: 'policy-revision-create-critical-case-quarantined',
    document: policyRevisionCreateCriticalCaseQuarantined,
  },
  {
    name: 'policy-revision-create-precondition-failed',
    document: policyRevisionCreatePreconditionFailed,
  },
  { name: 'policy-revision-create-unchanged', document: policyRevisionCreateUnchanged },
  { name: 'policy-revision-create-unknown-field', document: policyRevisionCreateUnknownField },
  { name: 'policy-revision-create-version-pointer', document: policyRevisionCreateVersionPointer },
  { name: 'policy-revision-first', document: policyRevisionFirst },
  { name: 'policy-revision-head', document: policyRevisionHead },
  { name: 'policy-revision-previous', document: policyRevisionPrevious },
  { name: 'policy-revision-published', document: policyRevisionPublished },
  { name: 'policy-revisions-list', document: policyRevisionsList },
  {
    name: 'policy-revisions-list-after-publish',
    document: policyRevisionsListAfterPublish,
  },
  {
    name: 'policy-revisions-list-conflict-head',
    document: policyRevisionsListConflictHead,
  },
  { name: 'project-create', document: projectCreate },
  { name: 'project-create-name-taken', document: projectCreateNameTaken },
  { name: 'project-detail', document: projectDetail },
  { name: 'project-detail-onboarding-connected', document: projectDetailOnboardingConnected },
  { name: 'project-detail-onboarding-empty', document: projectDetailOnboardingEmpty },
  { name: 'project-detail-onboarding-forbidden', document: projectDetailOnboardingForbidden },
  { name: 'project-detail-onboarding-project', document: projectDetailOnboardingProject },
  {
    name: 'project-detail-onboarding-project-not-found',
    document: projectDetailOnboardingProjectNotFound,
  },
  { name: 'project-detail-onboarding-run', document: projectDetailOnboardingRun },
  { name: 'project-update-precondition-failed', document: projectUpdatePreconditionFailed },
  // The evidence retention pair: the read's stored windows plus the two refusals a review can
  // select, and the upsert's stored windows (body closed, `RetentionPolicy`) with its out-of-range 422.
  { name: 'retention-read-ok', document: retentionReadOk },
  { name: 'retention-read-forbidden', document: retentionReadForbidden },
  { name: 'retention-read-not-found', document: retentionReadNotFound },
  { name: 'retention-put-ok', document: retentionPutOk },
  { name: 'retention-put-out-of-range', document: retentionPutOutOfRange },
  { name: 'projects-list', document: projectsList },
  { name: 'projects-list-onboarding-empty', document: projectsListOnboardingEmpty },
  { name: 'projects-list-onboarding-forbidden', document: projectsListOnboardingForbidden },
  { name: 'projects-list-unauthenticated', document: projectsListUnauthenticated },
  {
    name: 'projects-list-onboarding-project-not-found',
    document: projectsListOnboardingProjectNotFound,
  },
  { name: 'roles-list', document: rolesList },
];
