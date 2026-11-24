export function auditActionLabel(action: string): string {
  switch (action) {
    case 'auth.login':
      return $localize`:@@shared.audit.action.auth-login:Signed in`;
    case 'run.state_changed':
      return $localize`:@@shared.audit.action.run-state-changed:Run state changed`;
    case 'policy_revision.create':
      return $localize`:@@shared.audit.action.policy-revision-create:Policy revision created`;
    case 'artifact.download_denied':
      return $localize`:@@shared.audit.action.artifact-download-denied:Artifact download denied`;
    case 'membership.bind':
      return $localize`:@@shared.audit.action.membership-bind:Membership linked`;
    case 'release.bind':
      return $localize`:@@shared.audit.action.release-bind:Release linked`;
    case 'run.cancel':
      return $localize`:@@shared.audit.action.run-cancel:Run canceled`;
    case 'run.finalize':
      return $localize`:@@shared.audit.action.run-finalize:Run shard finalized`;
    case 'usage.invoice_worksheet_read':
      return $localize`:@@shared.audit.action.usage-invoice-worksheet-read:Invoice worksheet viewed`;
    case 'usage.statement_refused':
      return $localize`:@@shared.audit.action.usage-statement-refused:Usage statement refused`;
    case 'usage.statement_replayed':
      return $localize`:@@shared.audit.action.usage-statement-replayed:Existing usage statement returned`;
    case 'usage.statement_issued':
      return $localize`:@@shared.audit.action.usage-statement-issued:Usage statement issued`;
    case 'usage.cost_per_tenant_read':
      return $localize`:@@shared.audit.action.usage-cost-per-tenant-read:Tenant cost viewed`;
    case 'usage.entry_redated':
      return $localize`:@@shared.audit.action.usage-entry-redated:Usage entry date changed`;
    case 'usage.reconciliation_read':
      return $localize`:@@shared.audit.action.usage-reconciliation-read:Usage reconciliation viewed`;
    case 'usage.ledger_read':
      return $localize`:@@shared.audit.action.usage-ledger-read:Usage ledger viewed`;
    case 'usage.quota_raise':
      return $localize`:@@shared.audit.action.usage-quota-raise:Usage quota raised`;
    default:
      return action;
  }
}
