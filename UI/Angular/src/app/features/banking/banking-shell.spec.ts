import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { BankingShell } from './banking-shell';

describe('BankingShell', () => {
  it('renders three tab links', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(BankingShell);
    fixture.detectChanges();
    const tabs = fixture.nativeElement.querySelectorAll('nav a');
    expect(tabs.length).toBe(3);
    expect([...tabs].map((a: HTMLAnchorElement) => a.getAttribute('data-testid')))
      .toEqual(['tab-cash', 'tab-statements', 'tab-reconcile']);
  });
});
