import { InjectionToken, type Type } from '@angular/core';

export type LoginExtensionLoader = () => Promise<Type<unknown> | null>;

// File replacement removes this import edge entirely from production builds.
export const LOGIN_EXTENSION_LOADER = new InjectionToken<LoginExtensionLoader>('login-extension', {
  providedIn: 'root',
  factory: () => () => import('./dev-sign-in.component').then((m) => m.DevSignInComponent),
});
