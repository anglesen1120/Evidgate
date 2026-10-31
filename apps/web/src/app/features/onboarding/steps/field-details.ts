import type { ErrorEnvelopeDetail } from '@evidgate/sdk';

/**
 * One envelope `details` row (00 §9.2) with the field it names, so a page can link a server
 * refusal to the control that carries it instead of printing it beside the form.
 */
export interface FieldDetail {
  /** The path without its JSON-pointer prefix or index: `/repository_id[0]` → `repository_id`. */
  readonly field: string;
  /** The path exactly as the envelope carried it; server text, rendered as text only. */
  readonly path: string;
  readonly reason: string;
}

/**
 * Maps an envelope's `details` to the fields they name.
 *
 * The contract does not fix where a detail points: `createProject` names a body member
 * (`name`), while the CI and policy endpoints use a JSON pointer (`/repository_id`). Both
 * spellings reduce to the same key here, which is what lets one lookup serve every form.
 */
export function fieldDetails(details: readonly ErrorEnvelopeDetail[]): readonly FieldDetail[] {
  return details.map((detail) => {
    // `path` is optional in the wire schema: a row that names no field ("the body is too
    // large") still belongs in the summary list, and it links to no control.
    const path = detail.path ?? '';
    return {
      field:
        path
          .replace(/^\/+/, '')
          .split(/[[\]/.]/)
          .shift()
          ?.trim() ?? '',
      path,
      reason: detail.reason,
    };
  });
}

/**
 * The first reason an envelope gave for `field`, or `null`.
 *
 * A field shows one message: the first row is the one a screen reader reads from the control's
 * description, and the summary list keeps every row.
 */
export function reasonFor(details: readonly FieldDetail[], field: string): string | null {
  return details.find((detail) => detail.field === field)?.reason ?? null;
}

/**
 * The `aria-describedby` value of a control: the ids that resolve to a rendered element, joined.
 *
 * A page must never describe a control with an id no element carries, so a caller passes `null`
 * for a hint or error it does not render in the current state; the empty result removes the
 * attribute instead of leaving a reference that points nowhere.
 */
export function describedBy(...ids: readonly (string | null | undefined)[]): string | null {
  const present = ids.filter((id): id is string => typeof id === 'string' && id !== '');
  return present.length === 0 ? null : present.join(' ');
}
