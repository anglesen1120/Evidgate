import type { IngestTokenView } from '@evidgate/sdk';

export type ConnectionHealthState = 'healthy' | 'unhealthy' | 'unknown';

export interface CredentialHealth {
  id: string;
  label: string;
  status: IngestTokenView['status'];
  usable: boolean;
  detail: 'active' | 'rotating' | 'expired' | 'revoked' | 'rotated-ended';
}

export interface ConnectionHealth {
  state: ConnectionHealthState;
  reason: 'usable' | 'rotating-only' | 'truncated' | 'all-unusable' | 'no-credentials';
  credentials: readonly CredentialHealth[];
  lastTokenUseAt: string | null;
  lastTokenUseIsLowerBound: boolean;
}

/** Metadata-only snapshot: credential availability is not proof of a successful ingest. */
export function deriveConnectionHealth(input: {
  tokens: readonly IngestTokenView[];
  tokensTruncated: boolean;
  now: Date;
}): ConnectionHealth {
  const now = input.now.getTime();
  const credentials: CredentialHealth[] = [];
  let lastTokenUseAt: string | null = null;
  let lastTokenUseTime = -Infinity;

  for (const token of input.tokens) {
    let detail: CredentialHealth['detail'];
    switch (token.status) {
      case 'active':
        detail =
          token.expires_at == null || Date.parse(token.expires_at) > now ? 'active' : 'expired';
        break;
      case 'rotated':
        detail =
          token.overlap_ends_at != null && Date.parse(token.overlap_ends_at) > now
            ? 'rotating'
            : 'rotated-ended';
        break;
      case 'expired':
        detail = 'expired';
        break;
      case 'revoked':
        detail = 'revoked';
        break;
    }
    credentials.push({
      id: token.id,
      label: `${token.name} (${token.prefix})`,
      status: token.status,
      usable: detail === 'active' || detail === 'rotating',
      detail,
    });
    if (token.last_used_at != null) {
      const usedAt = Date.parse(token.last_used_at);
      if (usedAt > lastTokenUseTime) {
        lastTokenUseAt = token.last_used_at;
        lastTokenUseTime = usedAt;
      }
    }
  }

  let state: ConnectionHealthState;
  let reason: ConnectionHealth['reason'];
  if (credentials.some((credential) => credential.usable)) {
    state = 'healthy';
    reason = credentials.some((credential) => credential.usable && credential.detail === 'active')
      ? 'usable'
      : 'rotating-only';
  } else if (input.tokensTruncated) {
    state = 'unknown';
    reason = 'truncated';
  } else {
    state = 'unhealthy';
    reason = credentials.length === 0 ? 'no-credentials' : 'all-unusable';
  }

  return {
    state,
    reason,
    credentials,
    lastTokenUseAt,
    lastTokenUseIsLowerBound: input.tokensTruncated,
  };
}
