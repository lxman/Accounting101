import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementDetail } from './statement-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('StatementDetail', () => {
  it('loads a statement and lists its lines', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        provideCapabilities('bankrec.write'),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'b1']]) } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(StatementDetail);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/b1')).flush(
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31', openingBalance: 0,
        closingBalance: 100, lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: 'X1' }], status: 'Posted' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    expect(fixture.nativeElement.textContent).toContain('dep');
    ctrl.verify();
  });
});
