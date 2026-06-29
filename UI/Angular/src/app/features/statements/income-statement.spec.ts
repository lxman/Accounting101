import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { IncomeStatement } from './income-statement';
import { StatementsService } from '../../core/statements/statements.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { IncomeStatementResponse } from '../../core/statements/statement';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000008';

const mockResponse: IncomeStatementResponse = {
  from: '2025-01-01',
  to: '2025-12-31',
  revenue: {
    title: 'Revenue',
    lines: [{ accountId: 'r1', number: '4000', name: 'Service Revenue', amount: 50000 }],
    total: 50000,
  },
  expenses: {
    title: 'Expenses',
    lines: [
      { accountId: 'x1', number: '6000', name: 'Salaries', amount: 30000 },
      // negative net income scenario line for testing parens
      { accountId: 'x2', number: '6100', name: 'Rent', amount: -500 },
    ],
    total: 29500,
  },
  netIncome: 20500,
};

const mockNegativeNetIncome: IncomeStatementResponse = {
  ...mockResponse,
  netIncome: -5000,
};

describe('IncomeStatement component', () => {
  let svcStub: { incomeStatement: ReturnType<typeof vi.fn> };

  async function setup(response: IncomeStatementResponse) {
    svcStub = { incomeStatement: vi.fn().mockReturnValue(of(response)) };

    await TestBed.configureTestingModule({
      imports: [IncomeStatement],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: StatementsService, useValue: svcStub },
      ],
    }).compileComponents();

    const clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);

    const fixture = TestBed.createComponent(IncomeStatement);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('renders Revenue and Expenses sections', async () => {
    const fixture = await setup(mockResponse);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Revenue');
    expect(el.textContent).toContain('Expenses');
  });

  it('shows netIncome with $ symbol in the grand total row', async () => {
    const fixture = await setup(mockResponse);
    const el = fixture.nativeElement as HTMLElement;
    const netIncome = el.querySelector('[data-testid="net-income"]')?.textContent?.trim();
    expect(netIncome).toBe(formatMoney(20500, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
  });

  it('always sends both from and to to the service', async () => {
    const fixture = await setup(mockResponse);
    expect(svcStub.incomeStatement).toHaveBeenCalled();
    const [fromArg, toArg] = svcStub.incomeStatement.mock.calls[0];
    expect(fromArg).toBeTruthy();
    expect(toArg).toBeTruthy();
  });

  it('re-queries with both from and to when from changes', async () => {
    const fixture = await setup(mockResponse);
    const callsBefore = svcStub.incomeStatement.mock.calls.length;

    fixture.componentInstance.from.set('2025-03-01');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(svcStub.incomeStatement.mock.calls.length).toBeGreaterThan(callsBefore);
    const lastCall = svcStub.incomeStatement.mock.calls.at(-1)!;
    expect(lastCall[0]).toBe('2025-03-01');
    expect(lastCall[1]).toBeTruthy(); // to is always sent
  });

  it('re-queries with both from and to when to changes', async () => {
    const fixture = await setup(mockResponse);
    const callsBefore = svcStub.incomeStatement.mock.calls.length;

    fixture.componentInstance.to.set('2025-06-30');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(svcStub.incomeStatement.mock.calls.length).toBeGreaterThan(callsBefore);
    const lastCall = svcStub.incomeStatement.mock.calls.at(-1)!;
    expect(lastCall[0]).toBeTruthy(); // from is always sent
    expect(lastCall[1]).toBe('2025-06-30');
  });

  it('renders a parenthesized negative for a negative line amount', async () => {
    const fixture = await setup(mockResponse);
    const el = fixture.nativeElement as HTMLElement;
    // The -500 line should show as (500.00) per parens profile
    const expectedNeg = formatMoney(-500, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false });
    expect(expectedNeg).toBe('(500.00)');
    expect(el.textContent).toContain(expectedNeg);
  });

  it('shows netIncome with $ symbol even when negative', async () => {
    const fixture = await setup(mockNegativeNetIncome);
    const el = fixture.nativeElement as HTMLElement;
    const netIncome = el.querySelector('[data-testid="net-income"]')?.textContent?.trim();
    expect(netIncome).toBe(formatMoney(-5000, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
  });
});
