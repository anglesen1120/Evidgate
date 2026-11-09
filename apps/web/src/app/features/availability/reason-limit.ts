// The switch write body is capped at 4096 bytes, and the server answers 413 instead of cutting
// the reason. The cap applies to the serialized UTF-8 body, so JSON escaping and non-BMP
// characters cost more bytes than their code points suggest; both limits are enforced here.

/** The wire limit on the reason today; kept beside the byte budget so neither drifts alone. */
export const REASON_CODE_POINT_LIMIT = 2000;

/** `x-max-bytes` of the switch set body in `contracts/schemas/healing-switch-set.v1.json`. */
export const REQUEST_BODY_BYTE_LIMIT = 4096;

/** The two body shapes the store can send; `system` writes never leave this page. */
export type ReasonScope = 'tenant' | 'project';

const encoder = new TextEncoder();
const LONGEST_MODE = 'disabled';
const REASON_MEMBER = encoder.encode(',"reason":');

/** The body without its reason member, named exactly as `bodyFor` in the store builds it. */
function envelope(scope: ReasonScope, projectId: string): string {
  return scope === 'project'
    ? JSON.stringify({ scope: 'project', project_id: projectId, mode: LONGEST_MODE })
    : JSON.stringify({ scope: 'tenant', mode: LONGEST_MODE });
}

/** Bytes of the whole body the store would send for this reason, mode and scope. */
export function serializedBodyBytes(reason: string, scope: ReasonScope, projectId: string): number {
  return (
    encoder.encode(envelope(scope, projectId)).length +
    REASON_MEMBER.length +
    encoder.encode(JSON.stringify(reason)).length
  );
}

/** True when the trimmed reason breaks either limit, so the save must be refused locally. */
export function reasonExceedsLimit(reason: string, scope: ReasonScope, projectId: string): boolean {
  const trimmed = reason.trim();
  if ([...trimmed].length > REASON_CODE_POINT_LIMIT) return true;
  return serializedBodyBytes(trimmed, scope, projectId) > REQUEST_BODY_BYTE_LIMIT;
}
