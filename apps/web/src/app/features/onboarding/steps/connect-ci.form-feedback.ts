import type { Signal } from '@angular/core';

import { createFormFeedback, type FormFeedback } from '../../../shared/forms';
import {
  MAX_REF_ENTRIES,
  MAX_TOKEN_NAME_LENGTH,
  invalidRepositoryId,
  invalidRepositoryName,
  invalidTokenName,
  validConnectionLines,
} from './connect-ci.validation';

interface BindingDraft {
  readonly repositoryId: Signal<string>;
  readonly repositoryName: Signal<string>;
  readonly refs: Signal<string>;
  readonly workflows: Signal<string>;
}

type ServerReason = (field: string) => string | null;

/** Presentation-only mapping; the command handlers retain their authoritative domain guards. */
export function createBindingFeedback(draft: BindingDraft, reason: ServerReason): FormFeedback {
  const repositoryIdError = $localize`:@@onboarding.connect.repo-id-error:Enter a positive whole-number repository ID.`;
  const repositoryNameError = $localize`:@@onboarding.connect.repo-name-error:Enter the repository as owner/repo.`;
  const refsError = $localize`:@@onboarding.connect.refs-error:Enter between 1 and ${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: non-empty ref patterns.`;
  const workflowsError = $localize`:@@onboarding.connect.workflows-error:Enter between 1 and ${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: non-empty workflow refs.`;

  return createFormFeedback([
    {
      id: 'connect-oidc-repo-id',
      label: $localize`:@@onboarding.connect.repo-id:GitHub numeric repository ID`,
      hint: $localize`:@@onboarding.connect.repo-id-hint:Enter the repository's numeric GitHub ID, not its owner/repo name.`,
      error: () =>
        invalidRepositoryId(draft.repositoryId()) ? repositoryIdError : reason('repository_id'),
    },
    {
      id: 'connect-oidc-repo-name',
      label: $localize`:@@onboarding.connect.repo-name:Repository (owner/repo)`,
      hint: $localize`:@@onboarding.connect.repo-name-hint:Use the GitHub owner and repository name, for example acme/web.`,
      error: () =>
        invalidRepositoryName(draft.repositoryName())
          ? repositoryNameError
          : reason('repository_full_name'),
    },
    {
      id: 'connect-oidc-refs',
      label: $localize`:@@onboarding.connect.refs:Allowed Git ref patterns`,
      hint: $localize`:@@onboarding.connect.refs-hint:One per line, for example refs/heads/your-branch. Enter 1–${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: patterns.`,
      error: () =>
        !validConnectionLines(draft.refs()) ? refsError : reason('allowed_ref_patterns'),
    },
    {
      id: 'connect-oidc-workflows',
      label: $localize`:@@onboarding.connect.workflows:Allowed workflow refs`,
      hint: $localize`:@@onboarding.connect.workflows-hint:One per line, for example owner/repo/.github/workflows/ci.yml@refs/heads/your-branch. Enter 1–${MAX_REF_ENTRIES}:MAX_REF_ENTRIES: refs.`,
      error: () =>
        !validConnectionLines(draft.workflows()) ? workflowsError : reason('allowed_workflow_refs'),
    },
  ]);
}

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
