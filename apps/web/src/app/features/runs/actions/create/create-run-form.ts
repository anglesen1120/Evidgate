import type { ApiError } from '../../../../core/http/api-error';
import type { CreateRunInput } from '../../data/run-actions.types';
import { CREATE_RUN_COPY as copy } from './create-run.copy';

type Ref = NonNullable<CreateRunInput['ref']>;
/** The dialog's own form model; `createRunBody` maps it onto the SDK's wire payload. */
export interface CreateRunDraft {
  manifestId: string;
  releaseId: string;
  purpose: NonNullable<CreateRunInput['purpose']>;
  replayGrantId: string;
  components: string;
  kind: NonNullable<Ref['kind']> | '';
  refName: string;
  prNumber: string;
  headSha: string;
  baseSha: string;
  triggerEvent: NonNullable<Ref['trigger_event']> | '';
  isFork: boolean;
}
export type CreateRunField = keyof CreateRunDraft;
export type CreateRunTextField = Exclude<
  CreateRunField,
  'purpose' | 'kind' | 'triggerEvent' | 'isFork'
>;
export const EMPTY_CREATE_RUN_DRAFT: Readonly<CreateRunDraft> = {
  manifestId: '',
  releaseId: '',
  purpose: 'regular',
  replayGrantId: '',
  components: '',
  kind: '',
  refName: '',
  prNumber: '',
  headSha: '',
  baseSha: '',
  triggerEvent: '',
  isFork: false,
};
export const CREATE_RUN_TEXT_FIELDS = [
  { key: 'manifestId', label: copy.manifest, hint: copy.manifestHint, required: true },
  { key: 'releaseId', label: copy.release, hint: copy.releaseHint, required: false },
  { key: 'replayGrantId', label: copy.grant, hint: '', required: false },
  { key: 'refName', label: copy.refName, hint: '', required: false },
  { key: 'prNumber', label: copy.prNumber, hint: '', required: false },
  { key: 'headSha', label: copy.headSha, hint: '', required: false },
  { key: 'baseSha', label: copy.baseSha, hint: '', required: false },
  { key: 'components', label: copy.components, hint: copy.componentsHint, required: false },
] as const;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SHA = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/;

function componentsObject(text: string): CreateRunInput['components'] {
  if (!text.trim()) return undefined;
  try {
    const value: unknown = JSON.parse(text);
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? (value as NonNullable<CreateRunInput['components']>)
      : undefined;
  } catch {
    return undefined;
  }
}

export function createRunBody(draft: Readonly<CreateRunDraft>): CreateRunInput {
  const components = componentsObject(draft.components);
  const ref: Ref = {
    ...(draft.kind ? { kind: draft.kind } : {}),
    ...(draft.refName ? { name: draft.refName } : {}),
    ...(draft.prNumber.trim() ? { pr_number: Number(draft.prNumber) } : {}),
    ...(draft.headSha.trim() ? { head_sha: draft.headSha.trim() } : {}),
    ...(draft.baseSha.trim() ? { base_sha: draft.baseSha.trim() } : {}),
    ...(draft.triggerEvent ? { trigger_event: draft.triggerEvent } : {}),
    ...(draft.isFork ? { is_fork: true } : {}),
  };
  return {
    manifest_id: draft.manifestId.trim(),
    ...(draft.releaseId.trim() ? { release_id: draft.releaseId.trim() } : {}),
    ...(draft.purpose === 'replay' ? { purpose: 'replay' } : {}),
    ...(draft.replayGrantId.trim() ? { replay_grant_id: draft.replayGrantId.trim() } : {}),
    ...(components ? { components } : {}),
    ...(Object.keys(ref).length ? { ref } : {}),
  };
}

/** The contract's `details[].path` per form field, in its JSON-pointer or dotted form. */
const FIELD_PATHS: Readonly<Record<string, CreateRunField>> = {
  manifest_id: 'manifestId',
  release_id: 'releaseId',
  purpose: 'purpose',
  replay_grant_id: 'replayGrantId',
  components: 'components',
  ref: 'kind',
  'ref.kind': 'kind',
  'ref.name': 'refName',
  'ref.pr_number': 'prNumber',
  'ref.head_sha': 'headSha',
  'ref.base_sha': 'baseSha',
  'ref.trigger_event': 'triggerEvent',
  'ref.is_fork': 'isFork',
};

function pathField(path: string | undefined): CreateRunField | undefined {
  const normalized = path?.replace(/^\//, '').replaceAll('/', '.');
  if (!normalized) return undefined;
  if (normalized.startsWith('components.')) return 'components';
  return FIELD_PATHS[normalized];
}

export function createRunFieldError(
  field: CreateRunField,
  draft: Readonly<CreateRunDraft>,
  error: ApiError | null,
): string | null {
  const local = localFieldError(field, draft);
  if (local) return local;
  if (error?.kind !== 'validation' && error?.kind !== 'payload_too_large') return null;
  const details = error.details.filter((detail) => pathField(detail.path) === field);
  return details.length
    ? `${error.message} (${details.map((detail) => detail.reason).join(', ')})`
    : null;
}

function refNameTooLong(value: string): boolean {
  let length = 0;
  for (let index = 0; index < value.length; index++) {
    if (++length > 255) return true;
    if ((value.codePointAt(index) ?? 0) > 0xffff) index++;
  }
  return false;
}

function localFieldError(field: CreateRunField, draft: Readonly<CreateRunDraft>): string | null {
  if (field === 'manifestId' && !draft.manifestId.trim()) return copy.required;
  if (field === 'purpose' && draft.purpose === 'replay') return copy.replayHint;
  if (field === 'manifestId' || field === 'releaseId' || field === 'replayGrantId') {
    const value = draft[field].trim();
    if (value && !UUID.test(value)) return copy.uuid;
    if (field === 'replayGrantId' && value && draft.purpose !== 'replay') return copy.grantPurpose;
  }
  if (field === 'refName' && refNameTooLong(draft.refName)) return copy.refLength;
  if (field === 'prNumber') {
    const value = draft.prNumber.trim();
    if (
      (draft.kind === 'pull_request' && !value) ||
      (value && (!/^\d+$/.test(value) || !Number.isSafeInteger(Number(value)) || Number(value) < 1))
    )
      return copy.positiveInteger;
  }
  if (
    (field === 'headSha' || field === 'baseSha') &&
    draft[field].trim() &&
    !SHA.test(draft[field].trim())
  )
    return copy.sha;
  if (field === 'components' && draft.components.trim() && !componentsObject(draft.components))
    return copy.json;
  return null;
}

export function createRunRefusal(error: ApiError): string {
  switch (error.kind) {
    case 'idempotency_conflict':
      return copy.conflict;
    case 'not_found':
      return copy.notFound;
    case 'forbidden':
      return copy.forbidden;
    case 'payload_too_large':
      return copy.tooLarge;
    case 'network':
    case 'timeout':
      return copy.uncertain;
    default:
      break;
  }
  switch (error.code) {
    case 'manifest_invalid':
      return copy.manifestInvalid;
    case 'execution_key_conflict':
      return copy.executionConflict;
    case 'release_unresolvable':
      return copy.releaseUnresolvable;
    case 'release_not_active':
      return copy.releaseNotActive;
    case 'fork_run_not_bindable':
      return copy.forkNotBindable;
    case 'replay_grant_invalid':
      return copy.replayGrantInvalid;
    case 'replay_release_not_allowed':
      return copy.replayRelease;
    case 'cap_exceeded':
      return copy.cap;
    case 'request_invalid':
      return copy.invalid;
    default:
      return error.message;
  }
}
