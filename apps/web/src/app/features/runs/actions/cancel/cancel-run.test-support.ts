import { SessionStore } from '../../../../core/session/session.store';

export const DENY_CANCEL_SESSION = {
  provide: SessionStore,
  useValue: { has: () => false },
};
