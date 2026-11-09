export const HEALING_SWITCH_COPY = {
  title: $localize`:@@healing-switch.title:Healing switch`,
  description: $localize`:@@healing-switch.description:Healing proposes and applies repairs. The most restrictive scope wins: disabled over shadow, shadow over enabled.`,
  loading: $localize`:@@healing-switch.loading:Loading the healing switch…`,
  readFailed: $localize`:@@healing-switch.read-failed:The healing switch could not be loaded.`,
  retry: $localize`:@@healing-switch.retry:Reload healing switch`,
  forbidden: $localize`:@@healing-switch.forbidden:You do not have permission to view the healing switch.`,
  noProject: $localize`:@@healing-switch.no-project:Choose a project to view its healing switch.`,
  posture: $localize`:@@healing-switch.posture:Current posture`,
  resolvedPrefix: $localize`:@@healing-switch.resolved-prefix:Healing is currently`,
  decidedBySystem: $localize`:@@healing-switch.decided-by-system:The system scope decides this.`,
  decidedByTenant: $localize`:@@healing-switch.decided-by-tenant:The tenant scope decides this.`,
  decidedByProject: $localize`:@@healing-switch.decided-by-project:The project scope decides this.`,
  resolvedDefault: $localize`:@@healing-switch.resolved-default:No scope stores a switch, so healing stays in the default shadow mode.`,
  unavailableResolved: $localize`:@@healing-switch.unavailable-resolved:Healing is unavailable.`,
  unavailableExplanation: $localize`:@@healing-switch.unavailable-explanation:While healing is unavailable the switch is read-only. The service reports this state; no scope can change it from here.`,
  dependencies: $localize`:@@healing-switch.dependencies:Dependency health`,
  dependencyLlm: $localize`:@@healing-switch.dependency-llm:Model endpoint`,
  dependencyKnowledge: $localize`:@@healing-switch.dependency-knowledge:Knowledge store`,
  notObserved: $localize`:@@healing-switch.not-observed:Not observed`,
  healthHint: $localize`:@@healing-switch.health-hint:Not observed means no health probe has reported yet. It is not a healthy signal.`,
  scopeSystem: $localize`:@@healing-switch.scope-system:System scope`,
  scopeTenant: $localize`:@@healing-switch.scope-tenant:Tenant scope`,
  scopeProject: $localize`:@@healing-switch.scope-project:Project scope`,
  systemReadOnly: $localize`:@@healing-switch.system-read-only:Only the platform operator can change the system scope. It is read-only here.`,
  mode: $localize`:@@healing-switch.mode:Mode`,
  setMode: $localize`:@@healing-switch.set-mode:Set mode`,
  modeEnabled: $localize`:@@healing-switch.mode-enabled:Enabled`,
  modeShadow: $localize`:@@healing-switch.mode-shadow:Shadow`,
  modeDisabled: $localize`:@@healing-switch.mode-disabled:Disabled`,
  modeUnavailable: $localize`:@@healing-switch.mode-unavailable:Unavailable`,
  setBy: $localize`:@@healing-switch.set-by:Set by`,
  reason: $localize`:@@healing-switch.reason:Reason`,
  changedAt: $localize`:@@healing-switch.changed-at:Last changed`,
  version: $localize`:@@healing-switch.version:Version`,
  redacted: $localize`:@@healing-switch.redacted:Hidden`,
  notProvided: $localize`:@@healing-switch.not-provided:—`,
  noRow: $localize`:@@healing-switch.no-row:No switch is stored at this scope.`,
  reasonLabel: $localize`:@@healing-switch.reason-label:Reason (optional)`,
  reasonHint: $localize`:@@healing-switch.reason-hint:Up to 2000 characters. The reason is stored with the change.`,
  reasonTooLong: $localize`:@@healing-switch.reason-too-long:The reason is longer than 2000 characters. Shorten it to save.`,
  modeHint: $localize`:@@healing-switch.mode-hint:The most restrictive scope wins. Choose one of the three modes.`,
  save: $localize`:@@healing-switch.save:Save switch`,
  saving: $localize`:@@healing-switch.saving:Saving…`,
  saved: $localize`:@@healing-switch.saved:Switch saved. The posture is reloaded from the server.`,
  readOnly: $localize`:@@healing-switch.read-only:Read-only: changing the switch needs permission to configure healing.`,
  blockedBySystem: $localize`:@@healing-switch.blocked-by-system:Read-only: the system scope takes precedence over every lower scope.`,
  blockedByTenant: $localize`:@@healing-switch.blocked-by-tenant:Read-only: the tenant scope takes precedence over the project scope.`,
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
