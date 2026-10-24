// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1): the one provider call the composition root
// makes. It registers two things and starts nothing else: the `ErrorHandler` that turns Angular
// errors into `client.error` spans, and an app initializer that starts the telemetry chunk and
// hooks the two global failure events.
//
// The initializer returns nothing on purpose. Returning a promise would hold bootstrap until the
// chunk had loaded and the exporter had resolved a URL — a telemetry delay would become a
// first-paint delay, which is exactly the coupling the owner brief forbids. The chunk loads
// during the first milliseconds of the app and every call before it lands is a no-op.
import { DOCUMENT } from '@angular/common';
import {
  ErrorHandler,
  inject,
  makeEnvironmentProviders,
  provideAppInitializer,
  type EnvironmentProviders,
} from '@angular/core';

import { registerGlobalErrorListeners, TelemetryErrorHandler } from './error-reporter';
import { TelemetryService } from './telemetry.service';

/**
 * Providers for web telemetry: the replacing `ErrorHandler` plus the initializer that starts the
 * runtime and registers the global listeners. Add `provideTelemetry()` to `appConfig.providers`;
 * `index.html` supplies the endpoint and the sampling ratio through its two meta tags, and with
 * neither tag the app traces locally against a no-op exporter and makes no network call.
 */
export function provideTelemetry(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: ErrorHandler, useClass: TelemetryErrorHandler },
    provideAppInitializer(() => {
      const service = inject(TelemetryService);
      service.start();
      const view = inject(DOCUMENT).defaultView;
      if (view !== null) {
        registerGlobalErrorListeners(view, service);
      }
    }),
  ]);
}
