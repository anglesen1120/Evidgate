export function auditActionLabel(action: string): string {
  switch (action) {
    case 'auth.login':
      return $localize`:@@shared.audit.action.auth-login:Signed in`;
    case 'run.state_changed':
      return $localize`:@@shared.audit.action.run-state-changed:Run state changed`;
    case 'policy_revision.create':
      return $localize`:@@shared.audit.action.policy-revision-create:Policy revision created`;
    default:
      return action;
  }
}
