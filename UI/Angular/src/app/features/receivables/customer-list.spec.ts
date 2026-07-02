import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { CustomerList } from './customer-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { vi } from 'vitest';

describe('CustomerList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideCapabilities('ar.write'),
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    // verify is called per-test if ctrl is used
  });

  it('lists customers and creates one inline', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(CustomerList);
    f.detectChanges();
    ctrl
      .expectOne('http://localhost:5000/clients/C1/customers')
      .flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Acme Co');

    const cmp = f.componentInstance;
    cmp.newName.set('Beta LLC');
    cmp.newEmail.set('b@x.com');
    cmp.add();

    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/customers');
    expect(post.request.body).toEqual({ name: 'Beta LLC', email: 'b@x.com' });
    post.flush({ id: 'cu2', name: 'Beta LLC', email: 'b@x.com' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Beta LLC');
    expect(cmp.newName()).toBe(''); // form cleared
    ctrl.verify();
  });

  it('clicking a customer row navigates to the account screen', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(CustomerList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const row = f.nativeElement.querySelector('[data-testid="customer-row"]') as HTMLElement;
    row.click();
    expect(nav).toHaveBeenCalledWith(['/receivables/customers', 'cu1']);
    ctrl.verify();
  });
});
