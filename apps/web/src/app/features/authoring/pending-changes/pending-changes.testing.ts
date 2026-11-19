import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import type { ApiError } from '../../../core/http/api-error';
import type { AuthoringChangeListItem } from '@evidgate/sdk';

export const PROJECT = '018f0000-0000-7000-8000-000000000001';
export const ITEM = '018f0000-0000-7000-8000-000000000002';
export const CHANGE = '018f0000-0000-7000-8000-000000000003';
export function pendingRow(
  kind: AuthoringChangeListItem['change_type'] = 'comment',
): AuthoringChangeListItem {
  return {
    change_id: CHANGE,
    target_kind: 'checklist_item',
    target_id: ITEM,
    change_type: kind,
    payload:
      kind === 'comment'
        ? { body: '<script>plain text</script> ' + 'Long comment. '.repeat(80) }
        : kind === 'merge'
          ? { source_item_ids: [ITEM], survivor_item_id: ITEM }
          : kind === 'split'
            ? { new_items: [{ title: 'New split item' }] }
            : {},
    state: 'pending',
    source: 'manual',
    base_version: 1,
    author_membership_id: ITEM,
    reason: 'Review this change',
    created_at: '2026-11-19T00:00:00Z',
  };
}
export function pendingError(kind: ApiError['kind'], status: number): ApiError {
  return {
    kind,
    status,
    code: kind,
    message: 'The server refused the change',
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details: [],
  };
}
export class PendingUiHarness extends ComponentHarness {
  static hostSelector = 'app-pending-changes-page, app-checklist-review-tab';
  async text() {
    return (await this.host()).text();
  }
  async textAt(selector: string) {
    return (await this.locatorFor(selector)()).text();
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async set(selector: string, value: string) {
    await (await this.locatorFor(selector)()).setInputValue(value);
  }
  async disabled(selector: string) {
    return (await this.locatorFor(selector)()).getProperty<boolean>('disabled');
  }
  async focusControl(selector: string) {
    await (await this.locatorFor(selector)()).focus();
  }
  async focused(selector: string) {
    return (await this.locatorFor(selector)()).isFocused();
  }
}
export class PendingConsentHarness extends ComponentHarness {
  static hostSelector = '[role="alertdialog"]';
  async cancelFocused() {
    return (await this.locatorFor('[data-pending-cancel]')()).isFocused();
  }
  async confirm() {
    await (await this.locatorFor('[data-pending-confirm]')()).click();
  }
  async cancel() {
    await (await this.locatorFor('[data-pending-cancel]')()).click();
  }
  async escape() {
    await (await this.host()).sendKeys(TestKey.ESCAPE);
  }
  async text() {
    return (await this.host()).text();
  }
  async destructive() {
    return (
      (await (await this.locatorFor('[data-pending-confirm]')()).getAttribute('class'))?.includes(
        'bg-destructive',
      ) ?? false
    );
  }
}
