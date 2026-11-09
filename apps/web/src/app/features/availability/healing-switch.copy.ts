export const HEALING_SWITCH_COPY = {
  title: $localize`:@@healingswitch.title:Healing switch`,
  description: $localize`:@@healingswitch.description:Healing proposes and applies repairs. The most restrictive scope wins: disabled over shadow, shadow over enabled.`,
  loading: $localize`:@@healingswitch.loading:Loading the healing switch…`,
  readFailed: $localize`:@@healingswitch.read-failed:The healing switch could not be loaded.`,
  retry: $localize`:@@healingswitch.retry:Reload healing switch`,
  forbidden: $localize`:@@healingswitch.forbidden:You do not have permission to view the healing switch.`,
  noProject: $localize`:@@healingswitch.no-project:Choose a project to view its healing switch.`,
  posture: $localize`:@@healingswitch.posture:Current posture`,
  resolvedPrefix: $localize`:@@healingswitch.resolved-prefix:Healing is currently`,
  decidedBySystem: $localize`:@@healingswitch.decided-by-system:The system scope decides this.`,
  decidedByTenant: $localize`:@@healingswitch.decided-by-tenant:The tenant scope decides this.`,
  decidedByProject: $localize`:@@healingswitch.decided-by-project:The project scope decides this.`,
  resolvedDefault: $localize`:@@healingswitch.resolved-default:No scope stores a switch, so healing stays in the default shadow mode.`,
  unavailableResolved: $localize`:@@healingswitch.unavailable-resolved:Healing is unavailable.`,
  unavailableExplanation: $localize`:@@healingswitch.unavailable-explanation:While healing is unavailable the switch is read-only. The service reports this state; no scope can change it from here.`,
  dependencies: $localize`:@@healingswitch.dependencies:Dependency health`,
  dependencyLlm: $localize`:@@healingswitch.dependency-llm:Model endpoint`,
  dependencyKnowledge: $localize`:@@healingswitch.dependency-knowledge:Knowledge store`,
  notObserved: $localize`:@@healingswitch.not-observed:Not observed`,
  healthHint: $localize`:@@healingswitch.health-hint:Not observed means no health probe has reported yet. It is not a healthy signal.`,
  scopeSystem: $localize`:@@healingswitch.scope-system:System scope`,
  scopeTenant: $localize`:@@healingswitch.scope-tenant:Tenant scope`,
  scopeProject: $localize`:@@healingswitch.scope-project:Project scope`,
  systemReadOnly: $localize`:@@healingswitch.system-read-only:Only the platform operator can change the system scope. It is read-only here.`,
  mode: $localize`:@@healingswitch.mode:Mode`,
  setMode: $localize`:@@healingswitch.set-mode:Set mode`,
  modeEnabled: $localize`:@@healingswitch.mode-enabled:Enabled`,
  modeShadow: $localize`:@@healingswitch.mode-shadow:Shadow`,
  modeDisabled: $localize`:@@healingswitch.mode-disabled:Disabled`,
  modeUnavailable: $localize`:@@healingswitch.mode-unavailable:Unavailable`,
  setBy: $localize`:@@healingswitch.set-by:Set by`,
  reason: $localize`:@@healingswitch.reason:Reason`,
  changedAt: $localize`:@@healingswitch.changed-at:Last changed`,
  version: $localize`:@@healingswitch.version:Version`,
  redacted: $localize`:@@healingswitch.redacted:Hidden`,
  notProvided: $localize`:@@healingswitch.not-provided:—`,
  noRow: $localize`:@@healingswitch.no-row:No switch is stored at this scope.`,
  reasonLabel: $localize`:@@healingswitch.reason-label:Reason (optional)`,
  reasonHint: $localize`:@@healingswitch.reason-hint:Up to 2000 characters. The reason is stored with the change.`,
  reasonTooLong: $localize`:@@healingswitch.reason-too-long:The reason is longer than 2000 characters. Shorten it to save.`,
  modeHint: $localize`:@@healingswitch.mode-hint:The most restrictive scope wins. Choose one of the three modes.`,
  save: $localize`:@@healingswitch.save:Save switch`,
  saving: $localize`:@@healingswitch.saving:Saving…`,
  saved: $localize`:@@healingswitch.saved:Switch saved. The posture is reloaded from the server.`,
  readOnly: $localize`:@@healingswitch.read-only:Read-only: changing the switch needs permission to configure healing.`,
  blockedBySystem: $localize`:@@healingswitch.blocked-by-system:Read-only: the system scope takes precedence over every lower scope.`,
  blockedByTenant: $localize`:@@healingswitch.blocked-by-tenant:Read-only: the tenant scope takes precedence over the project scope.`,
};

/** Every mode a row or a resolution can name; `unavailable` is the platform's observation. */
export type HealingMode = 'enabled' | 'shadow' | 'disabled' | 'unavailable';

export function modeLabel(mode: HealingMode): string {
  switch (mode) {
    case 'enabled':
      return HEALING_SWITCH_COPY.modeEnabled;
    case 'shadow':
      return HEALING_SWITCH_COPY.modeShadow;
    case 'disabled':
      return HEALING_SWITCH_COPY.modeDisabled;
    case 'unavailable':
      return HEALING_SWITCH_COPY.modeUnavailable;
  }
}
