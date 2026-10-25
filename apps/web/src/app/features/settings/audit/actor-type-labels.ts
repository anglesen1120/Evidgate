// Shared by the filter and table so each actor label has one translation unit.
export const AUDIT_ACTOR_TYPE_LABELS = {
  user: $localize`:@@settings.audit.actor-user:User`,
  ci_producer: $localize`:@@settings.audit.actor-ci:CI producer`,
  agent: $localize`:@@settings.audit.actor-agent:Agent`,
  platform_operator: $localize`:@@settings.audit.actor-operator:Platform staff`,
  system: $localize`:@@settings.audit.actor-system:System`,
} as const;
