import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorList } from './vendor-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('VendorList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('lists vendors and creates one inline', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Acme Parts');

    const cmp = f.componentInstance;
    cmp.newName.set('Beta Supply');
    cmp.newEmail.set('b@x.com');
    cmp.add();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/vendors');
    expect(post.request.body).toEqual({ name: 'Beta Supply', email: 'b@x.com' });
    post.flush({ id: 'v2', name: 'Beta Supply', email: 'b@x.com' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Beta Supply');
    expect(cmp.newName()).toBe('');
    ctrl.verify();
  });

  it('clicking a vendor row selects it and navigates to bills', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(VendorList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    const row = f.nativeElement.querySelector('[data-testid="vendor-row"]') as HTMLElement;
    row.click();
    expect(svc.selectedVendorId()).toBe('v1');
    expect(nav).toHaveBeenCalledWith(['/payables/bills']);
    ctrl.verify();
  });
});
