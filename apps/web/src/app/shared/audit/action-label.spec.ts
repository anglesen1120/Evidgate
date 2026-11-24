import { clearTranslations, loadTranslations } from '@angular/localize';
import { auditActionLabel } from './action-label';

const knownActions = [
  ['auth.login', 'Signed in', 'Đăng nhập'],
  ['run.state_changed', 'Run state changed', 'Thay đổi trạng thái lượt chạy'],
  ['policy_revision.create', 'Policy revision created', 'Tạo phiên bản chính sách'],
  ['artifact.download_denied', 'Artifact download denied', 'Từ chối tải xuống hiện vật'],
  ['membership.bind', 'Membership linked', 'Liên kết tư cách thành viên'],
  ['release.bind', 'Release linked', 'Liên kết bản phát hành'],
  ['run.cancel', 'Run canceled', 'Hủy lượt chạy'],
  ['run.finalize', 'Run shard finalized', 'Hoàn tất phân đoạn của lượt chạy'],
  ['usage.invoice_worksheet_read', 'Invoice worksheet viewed', 'Xem bảng tính hóa đơn'],
  ['usage.statement_refused', 'Usage statement refused', 'Từ chối bảng kê sử dụng'],
  [
    'usage.statement_replayed',
    'Existing usage statement returned',
    'Trả lại bảng kê sử dụng hiện có',
  ],
  ['usage.statement_issued', 'Usage statement issued', 'Phát hành bảng kê sử dụng'],
  ['usage.cost_per_tenant_read', 'Tenant cost viewed', 'Xem chi phí của tổ chức'],
  ['usage.entry_redated', 'Usage entry date changed', 'Thay đổi ngày của mục sử dụng'],
  ['usage.reconciliation_read', 'Usage reconciliation viewed', 'Xem đối soát sử dụng'],
  ['usage.ledger_read', 'Usage ledger viewed', 'Xem sổ sử dụng'],
  ['usage.quota_raise', 'Usage quota raised', 'Tăng hạn mức sử dụng'],
] as const;

describe('auditActionLabel', () => {
  afterEach(() => clearTranslations());

  it.each(knownActions)('labels known action %s in English', (code, label) => {
    expect(auditActionLabel(code)).toBe(label);
  });

  it.each(knownActions)('labels known action %s in Vietnamese', (code, _english, label) => {
    loadTranslations({
      [`shared.audit.action.${code.replace(/[._]/g, '-')}`]: label,
    });
    expect(auditActionLabel(code)).toBe(label);
  });

  it.each(['future.event', '', 'toString', '__proto__', '<script>', ' auth.login ', 'AUTH.LOGIN'])(
    'preserves unknown code %s exactly',
    (code) => {
      expect(auditActionLabel(code)).toBe(code);
    },
  );

  it('preserves unknown codes when Vietnamese translations are loaded', () => {
    loadTranslations({ 'shared.audit.action.auth-login': 'Đăng nhập' });
    expect(auditActionLabel('auth.login')).toBe('Đăng nhập');
    expect(auditActionLabel('future.event')).toBe('future.event');
  });
});
