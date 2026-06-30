import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { ReceivablesShell } from './receivables-shell';

describe('ReceivablesShell', () => {
  it('renders Invoices / Payments / Customers tabs with routerLinks', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(ReceivablesShell); f.detectChanges();
    const el = f.nativeElement;
    for (const [tid, label, seg] of [
      ['tab-invoices', 'Invoices', 'invoices'],
      ['tab-payments', 'Payments', 'payments'],
      ['tab-customers', 'Customers', 'customers'],
    ] as const) {
      const a = el.querySelector(`[data-testid=${tid}]`) as HTMLAnchorElement;
      expect(a).toBeTruthy();
      expect(a.textContent.trim()).toBe(label);
      expect(a.getAttribute('href')).toContain(seg);
    }
  });

  it('renders the Credits tab linking to credits', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(ReceivablesShell); f.detectChanges();
    const tab = f.nativeElement.querySelector('[data-testid="tab-credits"]') as HTMLAnchorElement;
    expect(tab).toBeTruthy();
    expect(tab.textContent!.trim()).toBe('Credits');
    expect(tab.getAttribute('href')).toContain('credits');
  });
});
