import { signal } from '@angular/core';
import { createFormFeedback } from './form-feedback';

describe('createFormFeedback', () => {
  function setup() {
    const value = signal('');
    const secondError = signal<string | null>('Choose a permission.');
    const feedback = createFormFeedback([
      {
        id: 'token-name',
        label: 'Name',
        hint: 'Use a recognizable name.',
        error: () => (value().trim() ? null : 'Enter a name.'),
      },
      { id: 'token-permission', label: 'Permission', error: secondError },
    ]);
    return { value, secondError, feedback, name: feedback.fields[0]! };
  }

  it('keeps pristine typing quiet, then shows correction on blur and updates during correction', () => {
    const { value, name, feedback } = setup();
    value.set(' ');
    expect(name.touched()).toBe(false);
    expect(name.correction()).toBeNull();
    expect(name.invalid()).toBe(false);
    expect(name.ariaInvalid()).toBeNull();
    expect(name.describedBy()).toBe('token-name-hint');
    name.blur();
    expect(name.correction()).toBe('Enter a name.');
    expect(name.invalid()).toBe(true);
    expect(name.ariaInvalid()).toBe('true');
    expect(name.describedBy()).toBe('token-name-hint token-name-error');
    expect(feedback.summary()).toEqual([]);
    value.set('Build upload');
    expect(name.correction()).toBeNull();
    expect(name.ariaInvalid()).toBeNull();
    expect(name.describedBy()).toBe('token-name-hint');
    value.set('');
    expect(name.correction()).toBe('Enter a name.');
  });

  it('submit checks untouched fields and creates ordered links with the inline correction text', () => {
    const { feedback, value, secondError } = setup();
    expect(feedback.submit()).toBe(false);
    expect(feedback.submitAttempts()).toBe(1);
    expect(feedback.fields.every((field) => field.invalid())).toBe(true);
    expect(feedback.summary()).toEqual([
      { id: 'token-name', label: 'Name', message: 'Enter a name.' },
      { id: 'token-permission', label: 'Permission', message: 'Choose a permission.' },
    ]);
    value.set('Build upload');
    expect(feedback.summary()).toHaveLength(1);
    secondError.set(null);
    expect(feedback.submit()).toBe(true);
    expect(feedback.summary()).toEqual([]);
    expect(feedback.submitAttempts()).toBe(2);
  });

  it('reset clears interaction feedback without clearing the draft or reusing submit attempts', () => {
    const { feedback, name, value } = setup();
    value.set('Retained draft');
    name.blur();
    feedback.submit();
    feedback.reset();
    expect(feedback.submitted()).toBe(false);
    expect(name.touched()).toBe(false);
    expect(feedback.summary()).toEqual([]);
    expect(value()).toBe('Retained draft');
    feedback.submit();
    expect(feedback.submitAttempts()).toBe(2);
  });

  it('preserves supplementary and hint IDs without duplicates or dangling error IDs', () => {
    const error = signal<string | null>('Choose a later date.');
    const feedback = createFormFeedback([
      {
        id: 'expiry',
        label: 'Expiry',
        hint: 'Use UTC.',
        describedBy: 'expiry-context expiry-hint expiry-context',
        error,
      },
      { id: 'optional', label: 'Optional', error: () => undefined },
    ]);
    const expiry = feedback.fields[0]!;
    expect(expiry.describedBy()).toBe('expiry-context expiry-hint');
    expiry.blur();
    expect(expiry.describedBy()).toBe('expiry-context expiry-hint expiry-error');
    error.set(null);
    expect(expiry.describedBy()).toBe('expiry-context expiry-hint');
    const noDescription: string | null = feedback.fields[1]!.describedBy();
    expect(noDescription).toBeNull();
  });

  it('does not assign an unrelated refusal to another field', () => {
    const { feedback, secondError, name } = setup();
    secondError.set('You cannot assign this permission.');
    feedback.fields[1]!.blur();
    expect(name.invalid()).toBe(false);
    expect(name.correction()).toBeNull();
    expect(feedback.fields[1]!.correction()).toBe('You cannot assign this permission.');
  });

  it('refreshes submit-time validation even when a non-signal constraint changed', () => {
    let expired = false;
    const feedback = createFormFeedback([
      { id: 'expiry', label: 'Expiry', error: () => (expired ? 'Choose a later date.' : null) },
    ]);
    feedback.fields[0]!.blur();
    expect(feedback.fields[0]!.invalid()).toBe(false);
    expired = true;
    expect(feedback.submit()).toBe(false);
    expect(feedback.summary()[0]?.message).toBe('Choose a later date.');
    expect(feedback.fields[0]!.announceInline()).toBe(false);
    feedback.reset();
    expect(feedback.fields[0]!.announceInline()).toBe(true);
  });

  it('refreshes touched reactive-form corrections without submitting again', () => {
    let valid = false;
    const feedback = createFormFeedback([
      { id: 'count', label: 'Count', error: () => (valid ? null : 'Enter an integer.') },
    ]);
    const field = feedback.fields[0]!;
    field.blur();
    expect(field.invalid()).toBe(true);
    valid = true;
    field.refresh();
    expect(field.invalid()).toBe(false);
    expect(feedback.submitted()).toBe(false);
  });
});
