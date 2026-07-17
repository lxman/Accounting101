import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FiscalSettingsScreen } from './fiscal-settings';
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

describe('FiscalSettingsScreen', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('loads the current month and PUTs the chosen one via the DOM select', () => {
    seed('admin.fiscal'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettingsScreen);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();

    const c = f.componentInstance as FiscalSettingsScreen;
    expect(c.selected()).toBe(12);
    expect(c.months.length).toBe(12);

    // The rendered <select> reflects the loaded month via the [value] binding.
    const select = (f.nativeElement as HTMLElement)
      .querySelector('[data-testid=fye-select]') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(select.value).toBe('12');

    // Drive a real DOM change so the (change) handler + Number(...) coercion run end-to-end.
    select.value = '6';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    f.detectChanges();
    expect(c.selected()).toBe(6);          // NUMBER, proving Number(...) coercion in select()

    c.save();
    const req = http.expectOne(url);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ fiscalYearEndMonth: 6 });
    req.flush({ fiscalYearEndMonth: 6 });
    expect(c.saved()).toBe(true);
  });

  it('hides Save without admin.fiscal', () => {
    seed('gl.read'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(FiscalSettingsScreen);
    f.detectChanges();
    http.expectOne(url).flush({ fiscalYearEndMonth: 12 });
    f.detectChanges();
    const btn = (f.nativeElement as HTMLElement).querySelector('button');
    expect(btn).toBeNull();
  });
});
