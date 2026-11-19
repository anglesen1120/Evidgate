import type {
  ManualCaseDetailDto,
  ManualCaseStepDto,
  UpdateManualCaseRequest,
} from '@evidgate/sdk';
import { newUuid } from '../../../core/http/random-id';
import { isApiRequestError } from '../../../core/http/api-error';

// Only the two codes this route needs are shipped: importing `contracts/permissions.json` to look
// them up put every role name and description in the lazy chunk, which the fixture-leak gate
// rejects. The catalogue stays the source; `editor.model.spec.ts` pins both values.
export const EDITOR_READ = 'authoring:read';
export const EDITOR_WRITE = 'authoring:write';
export type CasePriority = ManualCaseDetailDto['priority'];
export type CaseTextField = 'title' | 'description' | 'precondition';
export type StepTextField = 'action' | 'expected' | 'data';
export interface DraftStep extends ManualCaseStepDto {
  readonly id: string;
}
export interface CaseDraft {
  readonly title: string;
  readonly description: string;
  readonly precondition: string;
  readonly priority: CasePriority;
  readonly steps: readonly DraftStep[];
}
export const CASE_FIELDS: readonly {
  key: CaseTextField;
  label: string;
  limit: number;
  required: boolean;
}[] = [
  {
    key: 'title',
    label: $localize`:@@authoring.editor.title-field:Title`,
    limit: 500,
    required: true,
  },
  {
    key: 'description',
    label: $localize`:@@authoring.editor.description:Description`,
    limit: 4000,
    required: false,
  },
  {
    key: 'precondition',
    label: $localize`:@@authoring.editor.preconditions:Preconditions`,
    limit: 4000,
    required: false,
  },
];
export const STEP_FIELDS: readonly { key: StepTextField; label: string; required: boolean }[] = [
  { key: 'action', label: $localize`:@@authoring.editor.action:Action`, required: true },
  {
    key: 'expected',
    label: $localize`:@@authoring.editor.expected:Expected result`,
    required: true,
  },
  { key: 'data', label: $localize`:@@authoring.editor.test-data:Test data`, required: false },
];
export const PRIORITIES: readonly { value: CasePriority; label: string }[] = [
  { value: 'critical', label: $localize`:@@authoring.editor.critical:Critical` },
  { value: 'high', label: $localize`:@@authoring.editor.high:High` },
  { value: 'medium', label: $localize`:@@authoring.editor.medium:Medium` },
  { value: 'low', label: $localize`:@@authoring.editor.low:Low` },
];
export const REASON_UNAVAILABLE = $localize`:@@authoring.editor.reason-unavailable:The current API cannot record a version reason for this operation. The reason field is unavailable; you can continue without a recorded reason.`;
export const WRITE_REASON = $localize`:@@authoring.editor.write-reason:Read-only: authoring:write permission is required to save or restore a case.`;
// Stops one code point past `limit` so a pasted megabyte costs the limit, not the paste.
export function codePoints(value: string, limit = Number.POSITIVE_INFINITY): number {
  let count = 0;
  for (let index = 0; index < value.length && count <= limit; index++, count++) {
    if ((value.codePointAt(index) ?? 0) > 0xffff) index++;
  }
  return count;
}
export const errorOf = (error: unknown) => (isApiRequestError(error) ? error.error : null);
export function draftOf(view: ManualCaseDetailDto): CaseDraft {
  return {
    title: view.title,
    description: view.description ?? '',
    precondition: view.precondition ?? '',
    priority: view.priority,
    steps: view.steps.map((step) => ({ ...step, id: newUuid() })),
  };
}
export function bodyOf(draft: CaseDraft): UpdateManualCaseRequest {
  return {
    title: draft.title,
    description: draft.description || null,
    precondition: draft.precondition || null,
    priority: draft.priority,
    steps: draft.steps.map(({ no, action, expected, data }) => ({
      no,
      action,
      expected,
      ...(data === undefined || data === null || data === '' ? {} : { data }),
    })),
  };
}
export function textError(value: string, limit: number, required = false): string | null {
  if (required && !value.trim())
    return $localize`:@@authoring.editor.required-error:Enter a value for this required field.`;
  if (codePoints(value, limit) > limit)
    return $localize`:@@authoring.editor.length-error:Shorten this field to the character limit shown below.`;
  return null;
}
export const SERVER_FIELD_ERROR = $localize`:@@authoring.editor.server-field-error:The server refused this field. Review its value before saving again.`;
