import { InjectionToken, type Type } from '@angular/core';

export type LoginExtensionLoader = () => Promise<Type<unknown> | null>;

// The production implementation has no import of development-only authentication code.
export const LOGIN_EXTENSION_LOADER = new InjectionToken<LoginExtensionLoader>('login-extension', {
  providedIn: 'root',
  factory: () => async () => null,
});
