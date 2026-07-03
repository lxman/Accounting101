import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetEditor } from './capability-set-editor';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { environment } from '../../core/api/environment';

function route(id: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } };
}
function seed(id: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users'), route(id)],
  });
}
const CATALOG = { capabilities: ['gl.read', 'gl.post', 'ar.write'], roles: [] };

describe('CapabilitySetEditor', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('creates a new set without a confirm step', () => {
    seed(null); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(CapabilitySetEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`).flush(CATALOG);
    f.detectChanges();
    const c = f.componentInstance as CapabilitySetEditor;
    c.setName('Warehouse'); c.toggleCapability('gl.read'); f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.save();
    // No confirm needed for a new set → POST fires immediately.
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('POST');
    req.flush({ id: 'x', name: 'Warehouse', capabilities: ['gl.read'], builtin: false, affectedMemberCount: 0 });
    expect(nav).toHaveBeenCalledWith(['/admin/access/sets']);
  });

  it('requires confirmation before editing a set that has members', () => {
    seed('s1'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(CapabilitySetEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`).flush(CATALOG);
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'Controller', capabilities: ['gl.read'], builtin: true, affectedMemberCount: 4 },
    ]);
    f.detectChanges();
    const c = f.componentInstance as CapabilitySetEditor;
    c.toggleCapability('gl.post');
    c.save();                       // does NOT PUT yet — asks to confirm
    f.detectChanges();
    http.expectNone(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(c.confirming()).toBe(true);
    expect(f.nativeElement.textContent).toContain('4');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.confirmSave();                // now it PUTs
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('PUT');
    req.flush({ id: 's1', name: 'Controller', capabilities: ['gl.read', 'gl.post'], builtin: true, affectedMemberCount: 4 });
    expect(nav).toHaveBeenCalledWith(['/admin/access/sets']);
  });
});
