import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { StatementDuplicateWarning } from './statement-duplicate-warning';
import { BankStatement } from '../../core/banking/banking';

function hit(over: Partial<BankStatement> = {}): BankStatement {
  return {
    id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
    openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted', ...over,
  };
}

function boot(duplicates: BankStatement[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(StatementDuplicateWarning);
  fixture.componentRef.setInput('duplicates', duplicates);
  fixture.detectChanges();
  return fixture;
}

describe('StatementDuplicateWarning', () => {
  it('renders nothing when there are no duplicates', () => {
    const fixture = boot([]);
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('lists each duplicate with a link to its statement', () => {
    const fixture = boot([hit(), hit({ id: 'b2', number: 'BST-00002' })]);
    const links = fixture.nativeElement.querySelectorAll('a');
    expect(links.length).toBe(2);
    expect(links[0].getAttribute('href')).toBe('/cash/statements/b1');
    expect(links[0].textContent).toContain('BST-00001');
    expect(fixture.nativeElement.textContent).toContain('2026-03-31');
  });

  it('falls back to a label when a statement has no number', () => {
    const fixture = boot([hit({ number: null })]);
    expect(fixture.nativeElement.querySelector('a').textContent).toContain('Statement');
  });
});
