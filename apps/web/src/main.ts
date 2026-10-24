import { bootstrapApplication } from '@angular/platform-browser';

import { App } from './app/app';
import { appConfig } from './app/app.config';

bootstrapApplication(App, appConfig).catch((error: unknown) => {
  // A failed bootstrap is a bug or an infrastructure failure, never a domain error: surface
  // it on the console so the E2E smoke test and the browser console both show it.
  console.error(error);
});
