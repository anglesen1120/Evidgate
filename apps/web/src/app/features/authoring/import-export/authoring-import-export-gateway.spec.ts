import { TestBed } from '@angular/core/testing';
import { AuthoringImportExportGateway } from '../../../core/data/authoring-import-export-gateway';
import { ApiGateway } from '../../../core/http/api-gateway';

describe('AuthoringImportExportGateway server filename', () => {
  const callWithResponse = vi.fn();
  let gateway: AuthoringImportExportGateway;
  beforeEach(() => {
    callWithResponse.mockReset();
    TestBed.configureTestingModule({
      providers: [{ provide: ApiGateway, useValue: { callWithResponse } }],
    });
    gateway = TestBed.inject(AuthoringImportExportGateway);
  });
  afterEach(() => TestBed.resetTestingModule());

  function response(disposition: string): Blob {
    const blob = new Blob(['{"rows":[]}']);
    callWithResponse.mockResolvedValue({
      data: blob,
      headers: { get: () => disposition },
    });
    return blob;
  }

  it.each([
    ['attachment; filename="authoring-export.csv"', 'authoring-export.csv'],
    ['attachment; filename=authoring-export.json', 'authoring-export.json'],
    [
      "attachment; filename=authoring-export.csv; filename*=UTF-8''authoring-export.csv",
      'authoring-export.csv',
    ],
    [
      'attachment; filename*=UTF-8\'\'authoring-export.xlsx; filename="authoring-export.xlsx"',
      'authoring-export.xlsx',
    ],
  ])('accepts the fixed filename from %s', async (header, filename) => {
    const blob = response(header);
    expect(await gateway.exportFile('project', { format: 'csv' })).toEqual({ blob, filename });
  });

  it.each([
    'attachment; filename="../authoring-export.csv"',
    'attachment; filename="C:\\authoring-export.csv"',
    'attachment; filename="authoring-export.html"',
    'attachment; filename="AUTHORING-EXPORT.csv"',
    'attachment; filename="authoring-export.csv\r\nX-Injected: yes"',
    'attachment; filename="authoring-export.csv"\r\nX-Injected: yes',
    'attachment; filename=authoring-export.csv; filename=authoring-export.json',
    "attachment; filename*=UTF-8''authoring-export.csv",
    'inline; filename=authoring-export.csv',
  ])('refuses hostile or ambiguous filename header %s', async (header) => {
    response(header);
    await expect(gateway.exportFile('project', { format: 'csv' })).rejects.toThrow(
      'fixed server filename',
    );
  });
});
