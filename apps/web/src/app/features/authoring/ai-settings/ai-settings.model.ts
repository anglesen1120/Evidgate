import type { AiSettingsDocument } from '@evidgate/sdk';
import schema from '../../../../../../../contracts/schemas/ai-settings.v1.json';

export const AI_READ = 'authoring:read';
export const AI_WRITE = 'project:write';
export type TextField =
  | 'description'
  | 'environment_url'
  | 'writing_style'
  | 'coverage_strategy'
  | 'critical'
  | 'high'
  | 'medium'
  | 'low'
  | 'exclusion_rules'
  | 'step_detail'
  | 'reason';
export const FIELDS: readonly { key: TextField; label: string; limit: number }[] = [
  {
    key: 'description',
    label: $localize`:@@authoring.aiSettings.description:Description`,
    limit: schema.properties.description.maxLength,
  },
  {
    key: 'environment_url',
    label: $localize`:@@authoring.aiSettings.environment:Environment URL`,
    limit: schema.properties.environment_url.maxLength,
  },
  {
    key: 'writing_style',
    label: $localize`:@@authoring.aiSettings.style:Writing style`,
    limit: schema.properties.writing_style.maxLength,
  },
  {
    key: 'coverage_strategy',
    label: $localize`:@@authoring.aiSettings.coverage:Coverage strategy`,
    limit: schema.properties.coverage_strategy.maxLength,
  },
  {
    key: 'critical',
    label: $localize`:@@authoring.aiSettings.critical:Critical priority criteria`,
    limit: schema.properties.priority_criteria.properties.critical.maxLength,
  },
  {
    key: 'high',
    label: $localize`:@@authoring.aiSettings.high:High priority criteria`,
    limit: schema.properties.priority_criteria.properties.high.maxLength,
  },
  {
    key: 'medium',
    label: $localize`:@@authoring.aiSettings.medium:Medium priority criteria`,
    limit: schema.properties.priority_criteria.properties.medium.maxLength,
  },
  {
    key: 'low',
    label: $localize`:@@authoring.aiSettings.low:Low priority criteria`,
    limit: schema.properties.priority_criteria.properties.low.maxLength,
  },
  {
    key: 'exclusion_rules',
    label: $localize`:@@authoring.aiSettings.exclusions:Exclusion rules`,
    limit: schema.properties.exclusion_rules.maxLength,
  },
  {
    key: 'step_detail',
    label: $localize`:@@authoring.aiSettings.detail:Step detail`,
    limit: schema.properties.step_detail.maxLength,
  },
  {
    key: 'reason',
    label: $localize`:@@authoring.aiSettings.reason:Save reason (optional)`,
    limit: 500,
  },
];
export function normalized(value: string): string {
  return value.normalize('NFC').trim();
}
export function count(value: string): number {
  return Array.from(normalized(value)).length;
}
export function textOf(document: AiSettingsDocument, key: TextField): string {
  if (key === 'reason') return '';
  if (key === 'critical' || key === 'high' || key === 'medium' || key === 'low')
    return document.priority_criteria[key];
  return document[key] ?? '';
}
export function replaceText(
  document: AiSettingsDocument,
  key: TextField,
  text: string,
): AiSettingsDocument {
  if (key === 'reason') return document;
  if (key === 'critical' || key === 'high' || key === 'medium' || key === 'low')
    return { ...document, priority_criteria: { ...document.priority_criteria, [key]: text } };
  return { ...document, [key]: text };
}
export function submission(document: AiSettingsDocument): AiSettingsDocument {
  const result = FIELDS.reduce(
    (result, field) => replaceText(result, field.key, normalized(textOf(document, field.key))),
    document,
  );
  return { ...result, environment_url: result.environment_url || null };
}
