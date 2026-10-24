// State sheet: docs/design/web/state-sheet.md#412-conflict-dialog
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
const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('ConflictDialogComponent', () => {
  beforeEach(async () => {
    // jsdom has no modal top layer. Stub only the browser lifecycle, not component behavior.
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
        this.querySelector<HTMLButtonElement>('button')?.focus();
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    await TestBed.configureTestingModule({
      imports: [ConflictDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    if (showModalDescriptor) {
      Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModalDescriptor);
    } else {
      Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    }
    if (closeDescriptor) {
      Object.defineProperty(HTMLDialogElement.prototype, 'close', closeDescriptor);
    } else {
      Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    }
    vi.restoreAllMocks();
  });

  function render(open = true) {
    const fixture = TestBed.createComponent(ConflictDialogComponent);
    fixture.componentRef.setInput('open', open);
    fixture.componentRef.setInput('fields', fields);
    fixture.componentRef.setInput('currentRevision', 'revision-18');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const dialog = root.querySelector('dialog');
    if (!dialog) throw new Error('Expected native dialog');
    return { fixture, root, dialog };
  }

  it('opens a named native modal and shows server vs local values with text diff markers', () => {
    const { root, dialog } = render();
    expect(dialog.open).toBe(true);
    expect(dialog.hasAttribute('open')).toBe(true);
    const title = root.querySelector(`#${dialog.getAttribute('aria-labelledby')}`);
    expect(title?.textContent).toBe('Review conflict');
    expect(root.textContent).toContain('My retained draft');
    expect(root.textContent).toContain('Current title');
    expect(root.textContent).toContain('revision-18');
    expect(root.textContent).toContain('Changed');
    expect(root.textContent).toContain('Same');
    expect(root.querySelector('button')?.textContent?.trim()).toBe('Close');
    expect(root.querySelector('form')).toBeNull();
  });

  it('exposes only reload and keepEditing, and explicit Reload emits only reload once', () => {
    const { fixture, root, dialog } = render();
    expect(
      reflectComponentType(ConflictDialogComponent)
        ?.outputs.map((item) => item.propName)
        .sort(),
    ).toEqual(['keepEditing', 'reload']);
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    expect(reload).not.toHaveBeenCalled();
    root.querySelector<HTMLButtonElement>('.reload')?.click();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(keepEditing).not.toHaveBeenCalled();
    expect(dialog.open).toBe(true);
    expect(fields.at(0)?.local).toBe('My retained draft');
    expect(root.textContent).toContain('Current title');
  });

  it('disables only Reload while loading and keeps dismissal and draft available', () => {
    const { fixture, root, dialog } = render();
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    fixture.componentRef.setInput('reloadState', 'loading');
    fixture.detectChanges();
    expect(root.querySelector<HTMLButtonElement>('.reload')?.disabled).toBe(true);
    root.querySelector<HTMLButtonElement>('.reload')?.click();
    expect(reload).not.toHaveBeenCalled();
    expect(root.querySelector('.body')?.getAttribute('aria-busy')).toBe('true');
    expect(dialog.hasAttribute('aria-busy')).toBe(false);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Reloading…');
    expect(root.querySelector<HTMLButtonElement>('footer button')?.disabled).toBe(false);
    root.querySelector<HTMLButtonElement>('footer button')?.click();
    expect(keepEditing).toHaveBeenCalledTimes(1);
    expect(dialog.open).toBe(false);
    expect(fields.at(0)?.local).toBe('My retained draft');
  });

  it.each(['cancel', 'escape', 'close', 'keep-editing', 'backdrop'])(
    '%s dismisses without applying anything and restores focus to the recorded opener',
    (action) => {
      const opener = document.createElement('button');
      document.body.append(opener);
      opener.focus();
      const { fixture, root, dialog } = render();
      const reload = vi.fn();
      const keepEditing = vi.fn();
      fixture.componentInstance.reload.subscribe(reload);
      fixture.componentInstance.keepEditing.subscribe(keepEditing);
      if (action === 'cancel') {
        const event = new Event('cancel', { cancelable: true });
        dialog.dispatchEvent(event);
        expect(event.defaultPrevented).toBe(true);
      } else if (action === 'escape') {
        dialog.dispatchEvent(
          new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
        );
      } else if (action === 'backdrop') {
        dialog.dispatchEvent(new MouseEvent('click', { clientX: -1, clientY: -1, bubbles: true }));
      } else {
        root
          .querySelector<HTMLButtonElement>(action === 'close' ? '.close' : 'footer button')
          ?.click();
      }
      expect(keepEditing).toHaveBeenCalledTimes(1);
      expect(reload).not.toHaveBeenCalled();
      expect(dialog.open).toBe(false);
      expect(document.activeElement).toBe(opener);
      expect(fields.at(0)?.local).toBe('My retained draft');
      fixture.destroy();
      opener.remove();
    },
  );

  it('does not dismiss for clicks inside dialog bounds or on comparison content', () => {
    const { fixture, root, dialog } = render();
    const keepEditing = vi.fn();
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    vi.spyOn(dialog, 'getBoundingClientRect').mockReturnValue(new DOMRect(10, 10, 200, 200));
    dialog.dispatchEvent(new MouseEvent('click', { clientX: 20, clientY: 20 }));
    root.querySelector('.local-value')?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(keepEditing).not.toHaveBeenCalled();
    expect(dialog.open).toBe(true);
  });

  it.each([
    { fields: [] },
    { fields: [{ id: 'title', label: 'Title', local: 'retained draft', server: null }] },
  ])(
    'shows a safe unavailable comparison without discarding the draft for $fields',
    ({ fields: value }) => {
      const { fixture, root } = render();
      fixture.componentRef.setInput('fields', value);
      fixture.componentRef.setInput('currentRevision', null);
      fixture.detectChanges();
      expect(root.textContent).toContain('The current version could not be compared');
      expect(root.textContent).toContain('Revision unavailable');
      expect(root.querySelector<HTMLButtonElement>('.reload')?.disabled).toBe(false);
      expect(root.querySelector<HTMLButtonElement>('footer button')?.disabled).toBe(false);
      if (value.length) {
        expect(root.textContent).toContain('retained draft');
        expect(root.textContent).toContain('Server value unavailable');
        expect(root.textContent).toContain('Not compared');
      }
    },
  );

  it('renders actual reload error metadata and all supplied values as text without retrying', () => {
    const { fixture, root } = render();
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
    fixture.detectChanges();
    expect(root.textContent).toContain(error.message);
    expect(root.textContent).toContain(error.code);
    expect(root.textContent).toContain(error.requestId);
    expect(root.textContent).toContain('<script>revision</script>');
    expect(root.textContent).toContain('<img>draft');
    expect(root.textContent).toContain('<svg>current');
    expect(root.querySelector('script, img, svg, b')).toBeNull();
    expect(root.querySelector('.body')?.getAttribute('aria-busy')).toBe('false');
    expect(reload).not.toHaveBeenCalled();
  });

  it('opens only on request and parent success closes without emitting a dismissal or reloading', () => {
    const { fixture, dialog } = render(false);
    const reload = vi.fn();
    const keepEditing = vi.fn();
    fixture.componentInstance.reload.subscribe(reload);
    fixture.componentInstance.keepEditing.subscribe(keepEditing);
    expect(dialog.open).toBe(false);
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
    expect(dialog.open).toBe(true);
    fixture.componentRef.setInput('open', false);
    fixture.detectChanges();
    expect(dialog.open).toBe(false);
    expect(reload).not.toHaveBeenCalled();
    expect(keepEditing).not.toHaveBeenCalled();
  });

  it('uses distinct heading identifiers for multiple dialog instances', () => {
    const first = render(false);
    const second = render(false);
    expect(first.dialog.getAttribute('aria-labelledby')).not.toBe(
      second.dialog.getAttribute('aria-labelledby'),
    );
  });
});
