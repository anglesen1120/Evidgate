/** The custom property the shell publishes so the layout layer can size its focus offset. */
export const TOPBAR_HEIGHT_PROPERTY = '--eg-topbar-height';

/**
 * Publishes the shell topbar's measured height as `--eg-topbar-height` on the document root.
 *
 * The stylesheet uses it as the `scroll-margin-top` of the main region's focusable elements
 * (`src/styles.css`), so a focused control clears the sticky bar instead of landing under it
 * (WCAG 2.4.11). A fixed token cannot do that job: the bar is a wrapping flex row, so its height
 * depends on the viewport width and on the length of the scope names it holds.
 *
 * Returns the teardown: it disconnects the observer and removes the property, so a destroyed shell
 * leaves no measurement behind for the next layout.
 */
export function trackTopbarHeight(doc: Document, topbar: HTMLElement): () => void {
  const root = doc.documentElement;
  const publish = (): void => {
    const height = topbar.getBoundingClientRect().height;
    // No layout (a hidden shell, jsdom) is no measurement: the stylesheet's own fallback stands.
    if (height <= 0) return;
    root.style.setProperty(TOPBAR_HEIGHT_PROPERTY, `${Math.ceil(height)}px`);
  };
  publish();
  const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(publish) : null;
  observer?.observe(topbar);
  return () => {
    observer?.disconnect();
    root.style.removeProperty(TOPBAR_HEIGHT_PROPERTY);
  };
}
