import { ComponentHarness } from '@angular/cdk/testing';
import type { AuthoringVersionDetailDto } from '@evidgate/sdk';

export const CASE_ID = 'a4430000-0000-4000-8000-000000000001';
export function version(no: number): AuthoringVersionDetailDto {
  return {
    version_id: 'version-' + no,
    entity_kind: 'manual_test_case',
    entity_id: CASE_ID,
    version_no: no,
    batch_id: 'batch-' + no,
    change_kind: no === 1 ? 'create' : 'update',
    source: 'manual',
    content_hash: 'sha256:' + String(no).repeat(64),
    author: null,
    reason: 'Reason ' + no,
    ai_run_id: null,
    created_at: '2026-11-' + (10 + no) + 'T00:00:00Z',
    content: {
      title: 'Title ' + no,
      steps: [{ no: 1, action: 'Action ' + no, expected: 'Expected ' + no }],
    },
  };
}
export class TimelineHarness extends ComponentHarness {
  static hostSelector = 'app-editor-timeline';
  readonly action = (name: string) => this.locatorFor(`button[aria-label="${name}"]`)();
  readonly actionIcon = (name: string) =>
    this.locatorFor(`button[aria-label="${name}"] hlm-icon`)();
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async ids() {
    return Promise.all(
      (await this.locatorForAll('[data-row-id]')()).map((row) => row.getAttribute('data-row-id')),
    );
  }
  async text() {
    return (await this.host()).text();
  }
  async disabled(selector: string) {
    const control = await this.locatorFor(selector)();
    return (
      (await control.getProperty<boolean>('disabled')) ||
      (await control.getAttribute('aria-disabled')) === 'true'
    );
  }
  async rowText() {
    return Promise.all((await this.locatorForAll('[data-row-id]')()).map((row) => row.text()));
  }
  async filter(value: string) {
    const input = await this.locatorFor('#case-history-filter')();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
}
export class RestoreHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"], [role="alertdialog"]';
  async confirm() {
    await (await this.locatorFor('[data-restore-confirm]')()).click();
  }
  async cancel() {
    await (await this.locatorFor('[data-restore-cancel]')()).click();
  }
  async reasonDisabled() {
    return (await this.locatorFor('#case-restore-reason')()).getProperty<boolean>('disabled');
  }
  async busy() {
    return (await this.locatorFor('[aria-busy]')()).getAttribute('aria-busy');
  }
  async text() {
    return (await this.host()).text();
  }
}
