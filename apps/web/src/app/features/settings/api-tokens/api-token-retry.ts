import type { Signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { map, of, startWith, switchMap, timer } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';

export function retryReadyFor(error: Signal<ApiError | null>): Signal<boolean> {
  return toSignal(
    toObservable(error).pipe(
      switchMap((failure) => {
        const seconds = failure?.retryAfterSeconds;
        return seconds != null && seconds > 0
          ? timer(seconds * 1000).pipe(
              map(() => true),
              startWith(false),
            )
          : of(true);
      }),
    ),
    { initialValue: true },
  );
}
