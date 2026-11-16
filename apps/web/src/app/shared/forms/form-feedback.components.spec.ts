import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { FieldFeedbackComponent } from './field-feedback.component';
import { FormErrorSummaryComponent } from './form-error-summary.component';
import { createFormFeedback } from './form-feedback';
import { FieldRequirementComponent, FormRequiredNoteComponent } from './form-requirement.component';

@Component({
  selector: 'app-form-feedback-test',
  imports: [
    HlmButton,
    HlmInput,
    HlmLabel,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    FieldRequirementComponent,
  ],
  template: `
    <form novalidate (submit)="submit($event)">
      <app-form-required-note />
      <app-form-error-summary [summaryId]="'test-form-errors'" [feedback]="feedback" />
      <label hlmLabel [for]="field.id">
        {{ field.label }} <app-field-requirement [required]="true" />
      </label>
      <input
        hlmInput
        required
        [id]="field.id"
        [value]="value()"
        [forceInvalid]="field.invalid()"
        [attr.aria-invalid]="field.ariaInvalid()"
        [aria-describedby]="field.describedBy()"
        (input)="edit($event)"
        (blur)="field.blur()"
      />
      <app-field-feedback [field]="field" />
      <button hlmBtn type="submit">{{ save }}</button>
    </form>
  `,
})
class FormFeedbackTestComponent {
  readonly value = signal('');
  readonly message = signal('Enter a repository ID.');
  readonly feedback = createFormFeedback([
    {
      id: 'feedback-repository',
      label: 'Repository ID',
      hint: 'Use the numeric repository ID.',
      error: () => (this.value() === '42' ? null : this.message()),
    },
  ]);
  readonly field = this.feedback.fields[0]!;
  readonly save = 'Save';
  commits = 0;

  edit(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.value.set(event.target.value);
  }

  submit(event: Event): void {
    event.preventDefault();
    if (this.feedback.submit()) this.commits += 1;
  }
}

describe('shared form feedback composition', () => {
  let fixture: ComponentFixture<FormFeedbackTestComponent>;
  let root: HTMLElement;
  let input: HTMLInputElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FormFeedbackTestComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(FormFeedbackTestComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    root = fixture.nativeElement as HTMLElement;
    input = root.querySelector('input')!;
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  async function submit(): Promise<void> {
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function edit(value: string): Promise<void> {
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('uses the HlmInput public ARIA inputs and retains guidance when blur reveals an error', async () => {
    expect(root.querySelector('label')?.htmlFor).toBe(input.id);
    expect(input.required).toBe(true);
    expect(input.getAttribute('aria-invalid')).not.toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('feedback-repository-hint');
    input.focus();
    input.dispatchEvent(new FocusEvent('blur'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(
      'feedback-repository-hint feedback-repository-error',
    );
    expect(root.querySelector('#feedback-repository-error')?.textContent).toBe(
      'Enter a repository ID.',
    );
    expect(root.querySelector('section')).toBeNull();
    expect(document.activeElement).toBe(input);
    await edit('42');
    expect(input.getAttribute('aria-invalid')).not.toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('feedback-repository-hint');
    expect(root.querySelector('#feedback-repository-error')).toBeNull();
  });

  it('focuses one linked summary after invalid submit, not on blur or correction', async () => {
    await submit();
    const summary = root.querySelector<HTMLElement>('section')!;
    expect(summary.getAttribute('data-slot')).toBe('alert');
    expect(summary.getAttribute('tabindex')).toBe('-1');
    expect(summary.getAttribute('role')).toBe('region');
    expect(document.activeElement).toBe(summary);
    const ids = [...root.querySelectorAll<HTMLElement>('[id]')].map((element) => element.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(root.querySelector('[role="alert"], [aria-live]')).toBeNull();
    const link = summary.querySelector('a')!;
    expect(link.getAttribute('href')).toBe('#feedback-repository');
    expect(link.textContent).toContain('Enter a repository ID.');
    link.click();
    expect(document.activeElement).toBe(input);
    fixture.componentInstance.message.set('Enter digits greater than zero.');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(input);
    expect(link.textContent).toContain('Enter digits greater than zero.');
    expect(fixture.componentInstance.value()).toBe('');
    await submit();
    expect(document.activeElement).toBe(summary);
    expect(fixture.componentInstance.commits).toBe(0);
  });

  it('does not steal focus after a valid submit or render an empty summary', async () => {
    await edit('42');
    const button = root.querySelector('button')!;
    button.focus();
    await submit();
    expect(root.querySelector('section')).toBeNull();
    expect(document.activeElement).toBe(button);
    expect(fixture.componentInstance.commits).toBe(1);
  });

  for (const theme of ['light', 'dark']) {
    it(`keeps long Vietnamese correction text complete and semantic tokens in ${theme}`, async () => {
      document.documentElement.setAttribute('data-theme', theme);
      const message = 'Nhập mã kho lưu trữ hợp lệ và kiểm tra quyền truy cập. '.repeat(12);
      fixture.componentInstance.message.set(message);
      await submit();
      const correction = root.querySelector<HTMLElement>('#feedback-repository-error')!;
      expect(correction.textContent).toBe(message);
      expect(correction.classList.contains('text-[var(--eg-danger-fg)]')).toBe(true);
      expect(root.querySelector('a')?.textContent).toContain(message);
      expect(root.querySelector('[title], .truncate, .line-clamp-2')).toBeNull();
    });
  }
});

describe('shared requiredness convention', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FieldRequirementComponent, FormRequiredNoteComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  it('defaults to the mostly-required note and marks only optional fields', () => {
    const note = TestBed.createComponent(FormRequiredNoteComponent);
    note.detectChanges();
    expect((note.nativeElement as HTMLElement).textContent).toContain(
      'All fields required unless marked optional.',
    );
    const marker = TestBed.createComponent(FieldRequirementComponent);
    marker.componentRef.setInput('required', false);
    marker.detectChanges();
    expect((marker.nativeElement as HTMLElement).textContent?.trim()).toBe('(optional)');
    marker.componentRef.setInput('required', true);
    marker.detectChanges();
    expect((marker.nativeElement as HTMLElement).textContent?.trim()).toBe('');
  });

  it('supports one explicit mostly-optional convention without asterisks', () => {
    const note = TestBed.createComponent(FormRequiredNoteComponent);
    note.componentRef.setInput('convention', 'mostly-optional');
    note.detectChanges();
    expect((note.nativeElement as HTMLElement).textContent).toContain(
      'All fields optional unless marked required.',
    );
    const marker = TestBed.createComponent(FieldRequirementComponent);
    marker.componentRef.setInput('convention', 'mostly-optional');
    marker.componentRef.setInput('required', true);
    marker.detectChanges();
    expect((marker.nativeElement as HTMLElement).textContent?.trim()).toBe('(required)');
    marker.componentRef.setInput('required', false);
    marker.detectChanges();
    expect((marker.nativeElement as HTMLElement).textContent?.trim()).toBe('');
  });
});
