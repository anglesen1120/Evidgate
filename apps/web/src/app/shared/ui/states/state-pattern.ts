import type { ApiError } from '../../../core/http/api-error';

/** Route owners choose the non-enumerating fixed panels before passing text to a generic error. */
export function patternForError(error: ApiError | null): 'forbidden' | 'not_found' | 'error' {
  switch (error?.kind) {
    case 'forbidden':
      return 'forbidden';
    case 'not_found':
      return 'not_found';
    default:
      return 'error';
  }
}
