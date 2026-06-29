import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementsService } from './statements.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { BalanceSheetResponse, IncomeStatementResponse } from './statement';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000006';

const mockSection = {
  title: 'Assets',
  lines: [{ accountId: 'a1', number: '1001', name: 'Cash', amount: 5000 }],
  total: 5000,
};

const mockBalanceSheet: BalanceSheetResponse = {
  asOf: '2025-12-31',
  assets: { ...mockSection, title: 'Assets' },
  liabilities: { ...mockSection, title: 'Liabilities', total: 2000 },
  equity: { ...mockSection, title: 'Equity', total: 3000 },
  totalAssets: 5000,
  totalLiabilitiesAndEquity: 5000,
  isBalanced: true,
};

const mockIncomeStatement: IncomeStatementResponse = {
  from: '2025-01-01',
  to: '2025-12-31',
  revenue: { ...mockSection, title: 'Revenue', total: 10000 },
  expenses: { ...mockSection, title: 'Expenses', total: 6000 },
  netIncome: 4000,
};

describe('StatementsService', () => {
  let service: StatementsService;
  let ctrl: HttpTestingController;
  let clientCtx: ClientContextService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(StatementsService);
    ctrl = TestBed.inject(HttpTestingController);
    clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);
  });

  afterEach(() => ctrl.verify());

  describe('balanceSheet()', () => {
    it('calls GET /clients/{id}/statements/balance-sheet without asOf when not provided', () => {
      service.balanceSheet().subscribe();
      const req = ctrl.expectOne(
        r => r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/balance-sheet`,
      );
      expect(req.request.method).toBe('GET');
      expect(req.request.params.has('asOf')).toBe(false);
      req.flush(mockBalanceSheet);
    });

    it('includes asOf query param when provided', () => {
      service.balanceSheet('2025-12-31').subscribe();
      const req = ctrl.expectOne(
        r =>
          r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/balance-sheet` &&
          r.params.get('asOf') === '2025-12-31',
      );
      expect(req.request.method).toBe('GET');
      req.flush(mockBalanceSheet);
    });

    it('passes the BalanceSheetResponse through', () => {
      let result: BalanceSheetResponse | undefined;
      service.balanceSheet('2025-12-31').subscribe(r => (result = r));
      ctrl
        .expectOne(r =>
          r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/balance-sheet`,
        )
        .flush(mockBalanceSheet);
      expect(result).toEqual(mockBalanceSheet);
    });
  });

  describe('incomeStatement()', () => {
    it('always sends both from and to query params', () => {
      service.incomeStatement('2025-01-01', '2025-12-31').subscribe();
      const req = ctrl.expectOne(
        r =>
          r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/income-statement` &&
          r.params.get('from') === '2025-01-01' &&
          r.params.get('to') === '2025-12-31',
      );
      expect(req.request.method).toBe('GET');
      expect(req.request.params.has('from')).toBe(true);
      expect(req.request.params.has('to')).toBe(true);
      req.flush(mockIncomeStatement);
    });

    it('passes the IncomeStatementResponse through', () => {
      let result: IncomeStatementResponse | undefined;
      service.incomeStatement('2025-01-01', '2025-12-31').subscribe(r => (result = r));
      ctrl
        .expectOne(r =>
          r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/income-statement`,
        )
        .flush(mockIncomeStatement);
      expect(result).toEqual(mockIncomeStatement);
    });

    it('uses the provided from date (not defaulted)', () => {
      service.incomeStatement('2025-03-01', '2025-03-31').subscribe();
      const req = ctrl.expectOne(
        r =>
          r.url === `${environment.apiBaseUrl}/clients/${clientId}/statements/income-statement`,
      );
      expect(req.request.params.get('from')).toBe('2025-03-01');
      expect(req.request.params.get('to')).toBe('2025-03-31');
      req.flush(mockIncomeStatement);
    });
  });
});
