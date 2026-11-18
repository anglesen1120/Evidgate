// Build constant, like shell/nav/product-phase.ts; not a caller-controlled route flag.
export const SECURITY_EVENTS_VERSION: 'V1' | 'V2' = 'V1';

export function securityEventVisible(
  type: string,
  version: 'V1' | 'V2' = SECURITY_EVENTS_VERSION,
): boolean {
  return version === 'V2' || type !== 'injection_attempt';
}

export const SECURITY_TYPE_LABELS = {
  auth_failure: $localize`:@@settings.security.type-auth:Authentication failure`,
  cross_tenant_probe: $localize`:@@settings.security.type-cross-tenant:Cross-tenant access probe`,
  secret_scanner_alert: $localize`:@@settings.security.type-scanner:Secret scanner alert`,
  kill_switch_used: $localize`:@@settings.security.type-kill-switch:Kill switch used`,
  support_access: $localize`:@@settings.security.type-support:Flagged support access`,
};
export const SECURITY_SEVERITY_LABELS = {
  info: $localize`:@@settings.security.severity-info:Info`,
  warning: $localize`:@@settings.security.severity-warning:Warning`,
  critical: $localize`:@@settings.security.severity-critical:Critical`,
};
export function securityTypeLabel(type: string): string {
  return Object.hasOwn(SECURITY_TYPE_LABELS, type)
    ? SECURITY_TYPE_LABELS[type as keyof typeof SECURITY_TYPE_LABELS]
    : type;
}
