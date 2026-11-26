import type { Signal } from '@angular/core';

import { createFormFeedback, type FormFeedback } from '../../../shared/forms';
import { MAX_TOKEN_NAME_LENGTH, invalidTokenName } from './connect-ci.validation';

type ServerReason = (field: string) => string | null;

export function createTokenFeedback(name: Signal<string>, reason: ServerReason): FormFeedback {
  const nameError = $localize`:@@onboarding.connect.token-name-error:Enter a token name between 1 and ${MAX_TOKEN_NAME_LENGTH}:MAX_TOKEN_NAME_LENGTH: characters.`;
  return createFormFeedback([
    {
      id: 'connect-token-name',
      label: $localize`:@@onboarding.connect.token-name:Token name`,
      hint: $localize`:@@onboarding.connect.token-name-hint:Choose a name that identifies this token in your CI setup.`,
      error: () => (invalidTokenName(name()) ? nameError : reason('name')),
    },
  ]);
}
