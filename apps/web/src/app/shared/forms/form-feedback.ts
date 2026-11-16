import { computed, signal, type Signal } from '@angular/core';

export interface FormFieldDefinition {
  /** Unique control ID, also used by its label and summary link. */
  readonly id: string;
  readonly label: string;
  readonly hint?: string;
  /** IDs of existing supplementary descriptions, not a replacement for the hint. */
  readonly describedBy?: string | null;
  readonly error: () => string | null | undefined;
}

export interface FieldFeedback {
  readonly id: string;
  readonly label: string;
  readonly hint: string | undefined;
  readonly hintId: string;
  readonly errorId: string;
  readonly touched: Signal<boolean>;
  readonly correction: Signal<string | null>;
  readonly invalid: Signal<boolean>;
  /** Bind with [attr.aria-invalid]; HlmInput's forceInvalid only drives its visual state. */
  readonly ariaInvalid: Signal<'true' | null>;
  readonly announceInline: Signal<boolean>;
  /** Recheck reactive-form controls after input; signal-backed validators update automatically. */
  readonly refresh: () => void;
  /** Bind to HlmInput's [aria-describedby], not [attr.aria-describedby]. */
  readonly describedBy: Signal<string | null>;
  readonly blur: () => void;
}

export interface FormErrorLink {
  readonly id: string;
  readonly label: string;
  readonly message: string;
}

export interface FormFeedback {
  readonly fields: readonly FieldFeedback[];
  readonly submitted: Signal<boolean>;
  readonly submitAttempts: Signal<number>;
  readonly summary: Signal<readonly FormErrorLink[]>;
  /** Call after refreshing submit-time validators; false prevents the mutation. */
  readonly submit: () => boolean;
  /** Reset interaction state on a new scope; the caller retains ownership of drafts. */
  readonly reset: () => void;
}

function describeField(
  definition: FormFieldDefinition,
  submitted: Signal<boolean>,
): {
  field: FieldFeedback;
  reset: () => void;
  refresh: () => void;
  error: Signal<string | null>;
} {
  const touched = signal(false);
  const revision = signal(0);
  const refresh = (): void => revision.update((value) => value + 1);
  const error = computed(() => {
    revision();
    return definition.error() || null;
  });
  const correction = computed(() => (touched() || submitted() ? error() : null));
  const invalid = computed(() => correction() !== null);
  const hintId = `${definition.id}-hint`;
  const errorId = `${definition.id}-error`;
  const describedBy = computed<string | null>(() => {
    const ids = [
      definition.describedBy,
      definition.hint ? hintId : undefined,
      correction() ? errorId : undefined,
    ].filter((id): id is string => Boolean(id));
    const tokens = [...new Set(ids.join(' ').split(/\s+/).filter(Boolean))];
    return tokens.length > 0 ? tokens.join(' ') : null;
  });
  return {
    field: {
      id: definition.id,
      label: definition.label,
      hint: definition.hint,
      hintId,
      errorId,
      touched: touched.asReadonly(),
      correction,
      invalid,
      ariaInvalid: computed<'true' | null>(() => (invalid() ? 'true' : null)),
      announceInline: computed(() => !submitted()),
      refresh,
      describedBy,
      blur: () => {
        touched.set(true);
        refresh();
      },
    },
    reset: () => touched.set(false),
    refresh,
    error,
  };
}

/** Validation stays with the form; this only coordinates when its corrections become visible. */
export function createFormFeedback(definitions: readonly FormFieldDefinition[]): FormFeedback {
  const submitted = signal(false);
  const submitAttempts = signal(0);
  const entries = definitions.map((definition) => describeField(definition, submitted));
  const summary = computed(() => {
    if (!submitted()) return [];
    return entries.flatMap(({ field, error }) => {
      const message = error();
      return message ? [{ id: field.id, label: field.label, message }] : [];
    });
  });
  return {
    fields: entries.map(({ field }) => field),
    submitted: submitted.asReadonly(),
    submitAttempts: submitAttempts.asReadonly(),
    summary,
    submit: () => {
      entries.forEach((entry) => entry.refresh());
      submitted.set(true);
      submitAttempts.update((attempt) => attempt + 1);
      return entries.every(({ error }) => error() === null);
    },
    reset: () => {
      submitted.set(false);
      entries.forEach((entry) => entry.reset());
    },
  };
}
