import { clearTranslations, loadTranslations } from '@angular/localize';
import { auditActionLabel } from './action-label';

describe('auditActionLabel', () => {
  afterEach(() => clearTranslations());

  it.each([
    ['auth.login', 'Signed in'],
    ['run.state_changed', 'Run state changed'],
    ['policy_revision.create', 'Policy revision created'],
  ])('labels known action %s in English', (code, label) => {
    expect(auditActionLabel(code)).toBe(label);
  });

  it('uses Vietnamese labels through Angular localization', () => {
    loadTranslations({
      'shared.audit.action.auth-login': 'Đăng nhập',
      'shared.audit.action.run-state-changed': 'Thay đổi trạng thái lượt chạy',
      'shared.audit.action.policy-revision-create': 'Tạo phiên bản chính sách',
    });
    expect(auditActionLabel('auth.login')).toBe('Đăng nhập');
    expect(auditActionLabel('run.state_changed')).toBe('Thay đổi trạng thái lượt chạy');
    expect(auditActionLabel('policy_revision.create')).toBe('Tạo phiên bản chính sách');
  });

  it.each(['future.event', '', 'toString', '<script>'])(
    'preserves unknown code %s exactly',
    (code) => {
      expect(auditActionLabel(code)).toBe(code);
    },
  );
});
