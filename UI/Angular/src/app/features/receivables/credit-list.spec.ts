import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CreditList } from './credit-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('ar.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const credit = (id: string, type: string, amount: number, memo: string | null, voided = false) =>
  ({ type, id, customerId: 'cu1', date: '2026-06-30', amount, memo, allocations: [{ targetId: 'inv1', amount }], voided });

function loadCustomerAndCredits(ctrl: HttpTestingController, f: any, rows: unknown[]) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits') && r.params.get('customerId') === 'cu1').flush(rows);
  f.detectChanges();
}

describe('CreditList', () => {
  it('loads credits for the selected customer and renders type/amount/memo', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'returned goods')]);
    const text = f.nativeElement.textContent;
    expect(text).toContain('Credit note');
    expect(text).toContain('100.00');
    expect(text).toContain('returned goods');
  });

  it('shows Void for credit-note/write-off and hides it for credit-application', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [
      credit('cn1', 'credit-note', 100, 'x'),
      credit('ca1', 'credit-application', 50, null),
    ]);
    const voidButtons = [...f.nativeElement.querySelectorAll('button')].filter(b => b.textContent.trim() === 'Void');
    expect(voidButtons.length).toBe(1);    // only the credit-note row
  });

  it('void posts to the right path and reloads', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.click();
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void').flush({});
    // reload
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([credit('cn1', 'credit-note', 100, 'x', true)]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Voided');
  });

  it('Record adjustment link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record adjustment') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/credits/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });

  it('navigates to the credit detail when a row is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/receivables/credits', 'credit-note', 'cn1']);
  });

  it('does not navigate when the Void button is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CreditList); f.detectChanges();
    loadCustomerAndCredits(ctrl, f, [credit('cn1', 'credit-note', 100, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
    // flush the void POST + the reload the void triggers, so HttpTestingController stays clean
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/credits')).flush([credit('cn1', 'credit-note', 100, 'x', true)]);
  });
});
