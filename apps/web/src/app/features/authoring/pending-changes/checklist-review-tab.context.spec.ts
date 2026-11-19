import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';
import { provideRouter } from '@angular/router';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import axe from 'axe-core';
import exceptionsDocument from '../../../../../e2e/a11y-exceptions.json';
import {
  AXE_JSDOM_RUN_OPTIONS,
  formatViolations,
  unexceptedViolations,
  type A11yException,
} from '../../../../../e2e/a11y/axe-config';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ChecklistReviewTabComponent } from './checklist-review-tab.component';
import { CHANGE, ITEM, PROJECT, PendingUiHarness, pendingRow } from './pending-changes.testing';

const A11Y_EXCEPTIONS: readonly A11yException[] = exceptionsDocument.exceptions;

registerLocaleData(localeVi);
provideLayoutTestStubs();

const AUTHOR = '018f0000-0000-7000-8000-000000000044';
const ROW: AuthoringChangeListItem = {
  ...pendingRow(),
  author_membership_id: AUTHOR,
  base_version: 7,
  created_at: '2026-11-19T12:34:56Z',
  reason: 'Retain the server-supplied reason',
  payload: { body: 'A server-supplied comment' },
};
const VI = {
  'authoring.review-context.parent-item': 'Mục danh sách kiểm tra cha',
  'authoring.review-context.priority': 'Độ ưu tiên của mục cha',
  'authoring.review-context.related-cases': 'Ca kiểm thử liên quan',
  'authoring.review-context.not-provided': 'Chưa được cung cấp',
  'authoring.review-context.author-id': 'Mã thành viên tác giả',
  'authoring.review-context.author-name': 'Tên hiển thị của tác giả',
  'authoring.review-context.source': 'Nguồn',
  'authoring.review-context.state': 'Trạng thái thay đổi',
  'authoring.review-context.applied': 'Đã áp dụng',
  'authoring.review-context.discarded': 'Đã loại bỏ',
  'authoring.review-context.reason': 'Lý do thay đổi',
  'authoring.pending-changes.change-id': 'Mã thay đổi',
  'authoring.pending-changes.manual-source': 'Thủ công',
  'authoring.pending-changes.ai-source': 'AI',
  'authoring.pending-changes.import-source': 'Nhập',
  'authoring.pending-changes.pending-state': 'Đang chờ',
  'authoring.pending-changes.base-version': 'Phiên bản gốc của mục đích',
  'authoring.pending-changes.queued-at': 'Xếp hàng lúc (UTC)',
  'authoring.pending-changes.reason-unset': 'Chưa đặt',
};

class ReviewContextHarness extends PendingUiHarness {
  async fact(scope: 'context' | 'fact', key: string) {
    return this.textAt(`[data-review-${scope}="${key}"]`);
  }
  async value(scope: 'context' | 'fact', key: string) {
    return this.textAt(`[data-review-${scope}="${key}"] dd`);
  }
  async timestamp() {
    return (await this.locatorFor('[data-review-fact="created-at"] time')()).getAttribute(
      'datetime',
    );
  }
}

describe.each(['en', 'vi'] as const)('Checklist review context in %s', (locale) => {
  const desktop = signal(true);
  const readable = signal(true);
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
  let previousTheme: string | null;
  let previousLang: string | null;

  beforeEach(() => {
    clearTranslations();
    if (locale === 'vi') loadTranslations(VI);
    previousTheme = document.documentElement.getAttribute('data-theme');
    previousLang = document.documentElement.getAttribute('lang');
    document.documentElement.setAttribute('lang', locale);
    desktop.set(true);
    readable.set(true);
    listPending.mockReset().mockResolvedValue({ items: [ROW], next_cursor: null });
    queue.mockReset();
    TestBed.configureTestingModule({
      imports: [ChecklistReviewTabComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LOCALE_ID, useValue: locale },
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: PROJECT, org_id: 'org' }),
            has: () => readable(),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => desktop(),
          },
        },
        { provide: AuthoringPendingDataService, useValue: { listPending, queue } },
      ],
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    clearTranslations();
    if (previousTheme === null) document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', previousTheme);
    if (previousLang === null) document.documentElement.removeAttribute('lang');
    else document.documentElement.setAttribute('lang', previousLang);
  });

  async function mount(row: AuthoringChangeListItem = ROW) {
    listPending.mockResolvedValue({ items: [row], next_cursor: null });
    const fixture = TestBed.createComponent(ChecklistReviewTabComponent);
    fixture.componentRef.setInput('projectId', PROJECT);
    fixture.componentRef.setInput('itemId', ITEM);
    await fixture.whenStable();
    return {
      fixture,
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewContextHarness),
    };
  }

  it('renders the labelled parent checklist item identifier with the shared copy primitive', async () => {
    const { ui } = await mount();
    expect(await ui.fact('context', 'parent-item')).toContain(
      locale === 'vi' ? VI['authoring.review-context.parent-item'] : 'Parent checklist item',
    );
    expect(await ui.value('context', 'parent-item')).toContain(ITEM);
    expect(await ui.count('[data-review-context="parent-item"] app-copyable-identifier')).toBe(1);
  });

  it('marks parent priority as Not provided instead of borrowing the case priority', async () => {
    const { ui } = await mount();
    expect(await ui.value('context', 'priority')).toBe(
      locale === 'vi' ? VI['authoring.review-context.not-provided'] : 'Not provided',
    );
  });

  it('marks related cases as Not provided instead of inventing an empty list or count', async () => {
    const { ui } = await mount();
    expect(await ui.value('context', 'related-cases')).toBe(
      locale === 'vi' ? VI['authoring.review-context.not-provided'] : 'Not provided',
    );
  });

  it('renders the supplied operation as the row heading', async () => {
    const { ui } = await mount();
    expect(await ui.textAt('[data-review-change] h3')).toBe('Comment');
  });

  it('renders the change identifier with the shared copy primitive', async () => {
    const { ui } = await mount();
    expect(await ui.value('fact', 'change-id')).toContain(CHANGE);
    expect(await ui.count('[data-review-fact="change-id"] app-copyable-identifier')).toBe(1);
  });

  it('renders the supplied author membership identifier without substituting a name', async () => {
    const { ui } = await mount();
    expect(await ui.value('fact', 'author-id')).toContain(AUTHOR);
    expect(await ui.fact('fact', 'author-id')).toContain(
      locale === 'vi' ? VI['authoring.review-context.author-id'] : 'Author membership id',
    );
    expect(await ui.count('[data-review-fact="author-id"] app-copyable-identifier')).toBe(1);
  });

  it('marks the unavailable author display name as Not provided', async () => {
    const { ui } = await mount();
    expect(await ui.value('fact', 'author-name')).toBe(
      locale === 'vi' ? VI['authoring.review-context.not-provided'] : 'Not provided',
    );
  });

  it.each([
    ['manual', 'Manual', 'Thủ công'],
    ['ai', 'AI', 'AI'],
    ['import', 'Import', 'Nhập'],
  ] as const)(
    'renders supplied source %s with a shared status primitive',
    async (source, en, vi) => {
      const { ui } = await mount({ ...ROW, source });
      expect(await ui.value('fact', 'source')).toContain(locale === 'vi' ? vi : en);
      expect(await ui.value('fact', 'source')).toContain(source);
      expect(await ui.count('[data-review-fact="source"] app-status-pill')).toBe(1);
    },
  );

  it.each([
    ['pending', 'Queued', 'Đang chờ'],
    ['applied', 'Applied', 'Đã áp dụng'],
    ['discarded', 'Discarded', 'Đã loại bỏ'],
  ] as const)(
    'renders supplied state %s without inferring case approval',
    async (state, en, vi) => {
      const { ui } = await mount({ ...ROW, state });
      expect(await ui.value('fact', 'state')).toContain(locale === 'vi' ? vi : en);
      expect(await ui.value('fact', 'state')).toContain(state);
      expect(await ui.count('[data-review-fact="state"] app-status-pill')).toBe(1);
    },
  );

  it('renders the supplied target base version with the shared number primitive', async () => {
    const { ui } = await mount();
    expect(await ui.value('fact', 'base-version')).toBe('7');
    expect(await ui.count('[data-review-fact="base-version"] app-number-value')).toBe(1);
  });

  it('renders the supplied creation instant as absolute UTC time', async () => {
    const { ui } = await mount();
    expect(await ui.timestamp()).toBe(ROW.created_at);
    expect(await ui.value('fact', 'created-at')).toContain('2026');
    expect(await ui.value('fact', 'created-at')).toContain('UTC');
  });

  it('renders the supplied change reason without replacing it with a label', async () => {
    const { ui } = await mount();
    expect(await ui.value('fact', 'reason')).toBe(ROW.reason);
  });

  it('keeps an absent optional reason distinct from contract facts that are Not provided', async () => {
    const { ui } = await mount({ ...ROW, reason: null });
    expect(await ui.value('fact', 'reason')).toBe(
      locale === 'vi' ? VI['authoring.pending-changes.reason-unset'] : 'Not set',
    );
  });

  it('renders the supplied comment as inert text below the provenance facts', async () => {
    const { ui } = await mount();
    expect(await ui.textAt('[data-review-change] [data-review-comment-body]')).toBe(
      'A server-supplied comment',
    );
  });

  it('uses a complete definition list for each row', async () => {
    const { ui } = await mount();
    expect(await ui.count('[data-review-change] dl')).toBe(1);
    expect(await ui.count('[data-review-change] dl dt')).toBe(8);
    expect(await ui.count('[data-review-change] dl dd')).toBe(8);
  });

  it('does not expose parent or row facts without authoring read permission', async () => {
    readable.set(false);
    const { ui } = await mount();
    expect(await ui.count('[data-review-context]')).toBe(0);
    expect(await ui.count('[data-review-change]')).toBe(0);
    expect(listPending).not.toHaveBeenCalled();
  });

  it.each(['light', 'dark'])(
    'has zero axe violations for context and provenance in %s',
    async (theme) => {
      document.documentElement.setAttribute('data-theme', theme);
      const { fixture, ui } = await mount();
      const root = fixture.nativeElement as HTMLElement;
      document.body.append(root);
      expect(await ui.count('[data-review-change] dl')).toBe(1);
      expect(await ui.count('[data-review-context="parent-item"]')).toBe(1);
      const result = await axe.run(root, AXE_JSDOM_RUN_OPTIONS);
      expect(result.passes.some((rule) => rule.id === 'definition-list')).toBe(true);
      expect(formatViolations(unexceptedViolations(result.violations, A11Y_EXCEPTIONS))).toEqual(
        [],
      );
    },
  );
});
