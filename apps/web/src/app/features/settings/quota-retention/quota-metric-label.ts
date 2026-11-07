import type { UsageSummaryMetricDto } from '@evidgate/sdk';

/**
 * The served metric keys, named for people. The ids stay in the `quota.*` namespace: this is the
 * quota feature's copy, not shared UI copy (`src/app/shared/ui` only declares `shared.*` ids).
 */
export const QUOTA_METRIC_LABELS: Readonly<Record<UsageSummaryMetricDto['metric'], string>> = {
  results: $localize`:@@quota.metric.results:Results`,
  artifact_gb_month: $localize`:@@quota.metric.artifact-gb-month:Artifact storage (GB-month)`,
  egress_gb: $localize`:@@quota.metric.egress-gb:Egress (GB)`,
  ai_tokens: $localize`:@@quota.metric.ai-tokens:AI tokens`,
  support_hours: $localize`:@@quota.metric.support-hours:Support hours`,
  runner_minutes: $localize`:@@quota.metric.runner-minutes:Runner minutes`,
  device_minutes: $localize`:@@quota.metric.device-minutes:Device minutes`,
};

export function quotaMetricLabel(metric: string): string {
  return Object.hasOwn(QUOTA_METRIC_LABELS, metric)
    ? QUOTA_METRIC_LABELS[metric as UsageSummaryMetricDto['metric']]
    : metric;
}
