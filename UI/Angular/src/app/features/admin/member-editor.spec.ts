import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberEditor } from './member-editor';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function route(id: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } };
}
function seed(id: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.users'), route(id)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

describe('MemberEditor (set-picker)', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('preselects the member\'s current sets and assigns the chosen ones', () => {
    seed('u1'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(MemberEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([
      { id: 's1', name: 'ArClerk', capabilities: ['ar.write'], builtin: true, affectedMemberCount: 0 },
      { id: 's2', name: 'Auditor', capabilities: ['audit.read'], builtin: true, affectedMemberCount: 0 },
    ]);
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`).flush([
      { userId: 'u1', roles: [], capabilities: ['ar.write'], grantedSetIds: ['s1'], setNames: ['ArClerk'] },
    ]);
    f.detectChanges();
    const c = f.componentInstance as MemberEditor;
    expect(c.selected().has('s1')).toBe(true);
    c.toggleSet('s2');
    c.save();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u1/sets`);
    expect(req.request.method).toBe('PUT');
    expect(new Set(req.request.body.setIds)).toEqual(new Set(['s1', 's2']));
    req.flush({ userId: 'u1', roles: [], capabilities: [], grantedSetIds: ['s1', 's2'], setNames: [] });
    expect(TestBed.inject(Router).url).toBeDefined();
  });

  it('removes the member and navigates back on success', () => {
    seed('u1'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(MemberEditor);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/capability-sets`).flush([]);
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`).flush([
      { userId: 'u1', roles: [], capabilities: [], grantedSetIds: [], setNames: [] },
    ]);
    f.detectChanges();
    const c = f.componentInstance as MemberEditor;
    c.remove();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u1`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
