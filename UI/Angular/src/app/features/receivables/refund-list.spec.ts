import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundList } from './refund-list';
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

const refund = (id: string, amount: number, memo: string | null, voided = false) =>
  ({ id, customerId: 'cu1', date: '2026-06-30', amount, memo, voided });

function loadCustomerAndRefunds(ctrl: HttpTestingController, f: any, rows: unknown[]) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  f.detectChanges();
  f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds') && r.params.get('customerId') === 'cu1').flush(rows);
  f.detectChanges();
}

describe('RefundList', () => {
  it('loads refunds for the selected customer and renders amount/memo', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'overpayment')]);
    const text = f.nativeElement.textContent;
    expect(text).toContain('50.00');
    expect(text).toContain('overpayment');
  });

  it('Void shows on non-voided rows, posts to the right path, and reloads', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    expect(voidBtn).toBeTruthy();
    voidBtn.click();
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([refund('rf1', 50, 'x', true)]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Voided');
  });

  it('hides Void on a voided row', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x', true)]);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void');
    expect(voidBtn).toBeUndefined();
  });

  it('Issue refund link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Issue refund') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/refunds/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });

  it('navigates to the refund detail when a row is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds', 'rf1']);
  });

  it('does not navigate when the Void button is clicked', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RefundList); f.detectChanges();
    loadCustomerAndRefunds(ctrl, f, [refund('rf1', 50, 'x')]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const voidBtn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent.trim() === 'Void') as HTMLButtonElement;
    voidBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).not.toHaveBeenCalled();
    // flush the void POST + reload the void triggers, so HttpTestingController stays clean
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void').flush({});
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/refunds')).flush([refund('rf1', 50, 'x', true)]);
  });
});
