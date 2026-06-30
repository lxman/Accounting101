import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CustomerSelect } from './customer-select';
import { ReceivablesService } from '../core/receivables/receivables.service';
import { ClientContextService } from '../core/client/client-context.service';

describe('CustomerSelect', () => {
  it('renders and maps a customer id to its name (itemToString), with id fallback', () => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(ReceivablesService);
    svc.load();
    TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers')
      .flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    const f = TestBed.createComponent(CustomerSelect); f.detectChanges();   // render smoke (template compiles)
    const cmp = f.componentInstance as CustomerSelect;
    expect(cmp.toName('cu1')).toBe('Acme Co');
    expect(cmp.toName('nope')).toBe('nope');                                // fallback to id
  });
});
