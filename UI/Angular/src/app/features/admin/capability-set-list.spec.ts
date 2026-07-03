import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetList } from './capability-set-list';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { environment } from '../../core/api/environment';

function seed() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users')],
  });
}

describe('CapabilitySetList', () => {
  let http: HttpTestingController;
  beforeEach(() => { seed(); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());

  it('lists sets with their member counts', () => {
    const f = TestBed.createComponent(CapabilitySetList);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'Controller', capabilities: ['gl.post'], builtin: true, affectedMemberCount: 3 },
    ]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Controller');
    expect(f.nativeElement.textContent).toContain('3');
  });

  it('deletes a set and refreshes', () => {
    const f = TestBed.createComponent(CapabilitySetList);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's2', name: 'Custom', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 },
    ]);
    f.detectChanges();
    (f.componentInstance as CapabilitySetList).remove({ id: 's2', name: 'Custom', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 });
    http.expectOne(`${environment.apiBaseUrl}/capability-sets/s2`).flush(null);
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([]);
  });
});
