import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import type { ApiError } from '../../../core/http/api-error';
import type { ConflictField } from './conflict-details';
import { ConflictDialogComponent } from './conflict-dialog.component';

const fields: readonly ConflictField[] = Object.freeze([
  Object.freeze({
    id: 'title',
    label: 'Title',
    local: 'My retained draft',
    server: 'Current title',
  }),
  Object.freeze({ id: 'notes', label: 'Notes', local: 'unchanged', server: 'unchanged' }),
]);

describe('ConflictDialogComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConflictDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    // jsdom has no layout; leave CDK's enabled/tabbable checks and focus trap intact.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  async function render(open = true) {
    const fixture = TestBed.createComponent(ConflictDialogComponent);
    fixture.componentRef.setInput('open', open);
    fixture.componentRef.setInput('fields', fields);
    fixture.componentRef.setInput('currentRevision', 'revision-18');
    await fixture.whenStable();
    return fixture;
  }

  function dialog(): HTMLElement {
    const element = document.querySelector<HTMLElement>('[role="dialog"]');
    if (!element) throw new Error('Expected Brain dialog overlay');
    return element;
  }

  function button(selector: string): HTMLButtonElement {
    const element = dialog().querySelector<HTMLButtonElement>(selector);
    if (!element) throw new Error(`Expected dialog button ${selector}`);
    return element;
  }

  it('opens a named modal with a description and separate local/server values', async () => {
    await render();
    const root = dialog();
    expect(root.getAttribute('aria-modal')).toBe('true');
    expect(document.getElementById(root.getAttribute('aria-labelledby') ?? '')?.textContent).toBe(
      'Review conflict',
    );
    expect(
      document.getElementById(root.getAttribute('aria-describedby') ?? '')?.textContent,
    ).toContain('Your draft is not merged');
    for (const text of ['My retained draft', 'Current title', 'revision-18', 'Changed', 'Same']) {
      expect(root.textContent).toContain(text);
    }
    expect(root.querySelector('form')).toBeNull();
    expect(button('.reload').hasAttribute('hlmBtn')).toBe(true);
  });

  it('preserves its public inputs and outputs; Reload emits only reload and keeps the draft', async () => {
    const fixture = await render();
    const metadata = reflectComponentType(ConflictDialogComponent);
    expect(metadata?.inputs.map((item) => item.propName).sort()).toEqual([
      'currentRevision',
      'fields',
      'open',
      'reloadError',
      'reloadState',
    ]);
    expect(metadata?.outputs.map((item) => item.propName).sort()).toEqual([
      'keepEditing',
      'reload',
    ]);
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    button('.reload').click();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(keepEditing).not.toHaveBeenCalled();
    expect(dialog().textContent).toContain('My retained draft');
  });

  it('disables Reload while loading and leaves dismissal and draft available', async () => {
    const fixture = await render();
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    fixture.componentRef.setInput('reloadState', 'loading');
    await fixture.whenStable();
    expect(button('.reload').disabled).toBe(true);
    button('.reload').click();
    expect(reload).not.toHaveBeenCalled();
    expect(dialog().querySelector('.body')?.getAttribute('aria-busy')).toBe('true');
    expect(dialog().querySelector('[role="status"]')?.textContent).toContain('Reloading…');
    expect(button('footer button').disabled).toBe(false);
    button('footer button').click();
    await fixture.whenStable();
    expect(keepEditing).toHaveBeenCalledTimes(1);
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(fields[0]?.local).toBe('My retained draft');
  });

  it.each(['escape', 'close', 'keep-editing', 'backdrop'])(
    '%s dismisses once without applying anything and restores the opener',
    async (action) => {
      const opener = document.createElement('button');
      document.body.append(opener);
      opener.focus();
      const fixture = await render();
      const reload = vi.fn();
      const keepEditing = vi.fn();
      fixture.componentInstance.reload.subscribe(reload);
      fixture.componentInstance.keepEditing.subscribe(keepEditing);
      button('.close').focus();
      if (action === 'escape') {
        button('.close').dispatchEvent(
          new KeyboardEvent('keydown', {
            key: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          }),
        );
      } else if (action === 'backdrop') {
        document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
      } else {
        button(action === 'close' ? '.close' : 'footer button').click();
      }
      await fixture.whenStable();
      expect(keepEditing).toHaveBeenCalledTimes(1);
      expect(reload).not.toHaveBeenCalled();
      expect(document.querySelector('[role="dialog"]')).toBeNull();
      expect(document.activeElement).toBe(opener);
      opener.remove();
    },
  );

  it('uses one CDK focus trap and wraps both keyboard boundaries, excluding disabled Reload', async () => {
    const fixture = await render();
    const anchors = document.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor');
    expect(anchors).toHaveLength(2);
    const first = button('.close');
    const last = button('.reload');
    // jsdom does not move focus for Tab. Focus the sentinels that native Tab/Shift+Tab reaches.
    last.focus();
    last.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    anchors[1]?.focus();
    expect(document.activeElement).toBe(first);
    first.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }),
    );
    anchors[0]?.focus();
    expect(document.activeElement).toBe(last);
    fixture.componentRef.setInput('reloadState', 'loading');
    await fixture.whenStable();
    anchors[0]?.focus();
    expect(document.activeElement).toBe(button('footer button'));
  });

  it('does not dismiss when comparison content is clicked', async () => {
    const fixture = await render();
    const keepEditing = vi.fn();
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    dialog()
      .querySelector('.local-value')
      ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();
    expect(keepEditing).not.toHaveBeenCalled();
    expect(dialog()).toBeTruthy();
  });

  it.each([
    { value: [] },
    { value: [{ id: 'title', label: 'Title', local: 'retained draft', server: null }] },
  ])('renders unavailable comparisons without discarding the draft', async ({ value }) => {
    const fixture = await render();
    fixture.componentRef.setInput('fields', value);
    fixture.componentRef.setInput('currentRevision', null);
    await fixture.whenStable();
    expect(dialog().textContent).toContain('The current version could not be compared');
    expect(dialog().textContent).toContain('Revision unavailable');
    expect(button('.reload').disabled).toBe(false);
    if (value.length) {
      expect(dialog().textContent).toContain('retained draft');
      expect(dialog().textContent).toContain('Server value unavailable');
      expect(dialog().textContent).toContain('Not compared');
    }
  });

  it('renders actual reload error metadata and supplied values as text without retrying', async () => {
    const fixture = await render();
    const error: ApiError = {
      kind: 'unavailable',
      status: 503,
      code: '<img>code',
      message: '<script>server error</script>',
      requestId: '<svg>request',
      retryable: true,
      details: [],
      retryAfterSeconds: null,
    };
    const reload = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentRef.setInput('reloadState', 'error');
    fixture.componentRef.setInput('reloadError', error);
    fixture.componentRef.setInput('currentRevision', '<script>revision</script>');
    fixture.componentRef.setInput('fields', [
      { id: 'x', label: '<b>Label</b>', local: '<img>draft', server: '<svg>current' },
    ]);
    await fixture.whenStable();
    for (const value of [
      error.message,
      error.code,
      error.requestId,
      '<script>revision</script>',
      '<img>draft',
    ]) {
      expect(dialog().textContent).toContain(value);
    }
    expect(dialog().querySelector('script, img, svg, b')).toBeNull();
    expect(dialog().querySelector('.body')?.getAttribute('aria-busy')).toBe('false');
    expect(reload).not.toHaveBeenCalled();
  });

  it('opens only on request; parent success closes without emitting dismissal or reload', async () => {
    const fixture = await render(false);
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    fixture.componentRef.setInput('open', true);
    await fixture.whenStable();
    expect(dialog()).toBeTruthy();
    fixture.componentRef.setInput('open', false);
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(reload).not.toHaveBeenCalled();
    expect(keepEditing).not.toHaveBeenCalled();
  });

  it('assigns distinct accessible titles to multiple dialog instances', async () => {
    await render();
    const first = dialog().getAttribute('aria-labelledby');
    await render();
    const dialogs = document.querySelectorAll('[role="dialog"]');
    expect(dialogs).toHaveLength(2);
    expect(dialogs[1]?.getAttribute('aria-labelledby')).not.toBe(first);
  });
});
