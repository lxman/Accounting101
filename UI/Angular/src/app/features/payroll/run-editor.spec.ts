import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { RunEditor } from './run-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('payroll.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RunEditor', () => {
  it('warns and disables Save when net pay is negative', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.gross.set(100); cmp.employeeFica.set(50); cmp.incomeTaxWithheld.set(40); cmp.deductions.set(30); // net = -20
    f.detectChanges();
    expect(cmp.net()).toBe(-20);
    expect(cmp.canSave()).toBe(false);
    expect(f.nativeElement.textContent).toContain('Net pay is negative');
    ctrl.verify();
  });

  it('posts the run and navigates to its detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RunEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.gross.set(1000); cmp.employeeFica.set(76.5); cmp.employerFica.set(76.5);
    cmp.deductions.set(50); cmp.incomeTaxWithheld.set(120); cmp.payDate.set('2026-06-30'); cmp.memo.set('June');
    f.detectChanges();
    expect(cmp.net()).toBe(753.5);
    expect(cmp.canSave()).toBe(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs');
    expect(req.request.body).toEqual({ gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June' });
    req.flush({ id: 'r9', number: 'PR-9', gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June', status: 'Posted' });
    expect(nav).toHaveBeenCalledWith(['/payroll/runs', 'r9']);
    ctrl.verify();
  });
});
