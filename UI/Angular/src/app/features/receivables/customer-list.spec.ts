import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CustomerList } from './customer-list';
import { ClientContextService } from '../../core/client/client-context.service';

describe('CustomerList', () => {
  let ctrl: HttpTestingController;

  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }

  afterEach(() => ctrl.verify());

  it('lists customers and creates one inline', () => {
    setup();
    const f = TestBed.createComponent(CustomerList);
    f.detectChanges();
    TestBed.inject(HttpTestingController)
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
  });
});
