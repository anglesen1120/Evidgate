import type { OverlayRef } from '@angular/cdk/overlay';
import type { BrnDialogRef } from '@spartan-ng/brain/dialog';

/** Brain routes Escape, backdrop, button and template closes through this instance method.
 * Gate it before the closing phase, leaving forceClose (e.g. teardown) and the CDK trap intact.
 */
export function guardDialogClose<T>(
  ref: BrnDialogRef<T>,
  overlay: OverlayRef,
  allow: () => boolean,
): () => void {
  let outsideClick: MouseEvent | undefined;
  const outside = overlay.outsidePointerEvents().subscribe((event) => {
    outsideClick = event.target === overlay.backdropElement ? event : undefined;
  });
  const dismiss = ref.dismiss.bind(ref);
  ref.dismiss = (reason) => {
    // CDK dispatches the same click outside during capture and at the backdrop during bubbling.
    // A denied close leaves the overlay alive for both; ask only once for that physical click.
    if (reason === 'backdrop' && outsideClick && outsideClick.eventPhase !== Event.NONE)
      return false;
    return dismiss(reason);
  };
  const close = ref.close.bind(ref);
  ref.close = (result) => {
    if (ref.open && allow()) close(result);
  };
  return () => {
    outside.unsubscribe();
    ref.dismiss = dismiss;
    ref.close = close;
  };
}
