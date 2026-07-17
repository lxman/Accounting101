import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FiscalSettings } from './fiscal-settings';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed(...caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

const url = `${environment.apiBaseUrl}/admin/clients/c1/fiscal-year-end`;

describe('FiscalSettings', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('loads the current month and PUTs the chosen one', () => {
    seed('admin.fiscal'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettings);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();

    const c = f.componentInstance as FiscalSettings;
    expect(c.selected()).toBe(12);
    expect(c.months.length).toBe(12);

    c.selected.set(6);
    c.save();
    const req = http.expectOne(url);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ fiscalYearEndMonth: 6 });
    req.flush({ fiscalYearEndMonth: 6 });
    expect(c.saved()).toBe(true);
  });

  it('hides Save without admin.fiscal', () => {
    seed('gl.read'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettings);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();
    const btn = (f.nativeElement as HTMLElement).querySelector('button');
    expect(btn).toBeNull();
  });
});
