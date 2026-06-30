import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorSelect } from './vendor-select';
import { PayablesService } from '../core/payables/payables.service';
import { ClientContextService } from '../core/client/client-context.service';

describe('VendorSelect', () => {
  it('renders vendor options from the service', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(PayablesService);
    const ctrl = TestBed.inject(HttpTestingController);
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    const f = TestBed.createComponent(VendorSelect);
    f.detectChanges();
    expect(f.componentInstance.svc.vendors()[0].name).toBe('Acme Parts');
    ctrl.verify();
  });
});
