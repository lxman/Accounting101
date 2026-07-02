import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberEditor } from './member-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { CapabilityCatalog } from '../../core/members/member';

function route(userId: string | null) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => userId } } } };
}

const catalog: CapabilityCatalog = {
  capabilities: ['ar.read', 'ar.write', 'ap.read', 'ap.write'],
  roles: [
    { role: 'ArClerk', capabilities: ['ar.read', 'ar.write'] },
    { role: 'ApClerk', capabilities: ['ap.read', 'ap.write'] },
  ],
};

function setup(userId: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('admin.users'), route(userId)],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  return ctrl;
}

describe('MemberEditor', () => {
  it('checking a role preset unions its capabilities into the working set', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(MemberEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/capabilities/catalog').flush(catalog);
    f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.capabilities().has('ar.read')).toBe(false);
    cmp.togglePreset(catalog.roles[0]);
    f.detectChanges();
    expect(cmp.capabilities().has('ar.read')).toBe(true);
    expect(cmp.capabilities().has('ar.write')).toBe(true);
    expect(cmp.checkedRoles().has('ArClerk')).toBe(true);
    // Unchecking the preset leaves capabilities as-is
    cmp.togglePreset(catalog.roles[0]);
    expect(cmp.checkedRoles().has('ArClerk')).toBe(false);
    expect(cmp.capabilities().has('ar.read')).toBe(true);
  });

  it('new mode: Save POSTs the working userId/roles/capabilities and navigates', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(MemberEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/capabilities/catalog').flush(catalog);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.userId.set('00000000-0000-0000-0000-000000000009');
    cmp.togglePreset(catalog.roles[0]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/members');
    expect(post.request.body.userId).toBe('00000000-0000-0000-0000-000000000009');
    expect(post.request.body.roles).toEqual(['ArClerk']);
    expect(post.request.body.capabilities).toEqual(expect.arrayContaining(['ar.read', 'ar.write']));
    post.flush({ userId: '00000000-0000-0000-0000-000000000009', roles: ['ArClerk'], capabilities: ['ar.read', 'ar.write'] });
    expect(nav).toHaveBeenCalledWith(['/admin/users']);
  });

  it('existing mode: preloads roles/capabilities and Save PUTs them', () => {
    const userId = '00000000-0000-0000-0000-000000000004';
    const ctrl = setup(userId);
    const f = TestBed.createComponent(MemberEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/capabilities/catalog').flush(catalog);
    ctrl.expectOne('http://localhost:5000/clients/C1/members').flush([
      { userId, roles: ['ArClerk'], capabilities: ['ar.read', 'ar.write'] },
    ]);
    f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.capabilities().has('ar.read')).toBe(true);
    expect(cmp.checkedRoles().has('ArClerk')).toBe(true);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const put = ctrl.expectOne(`http://localhost:5000/clients/C1/members/${userId}`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.roles).toEqual(['ArClerk']);
    put.flush({ userId, roles: ['ArClerk'], capabilities: ['ar.read', 'ar.write'] });
    expect(nav).toHaveBeenCalledWith(['/admin/users']);
  });

  it('surfaces the 409 last-admin detail on save failure', () => {
    const userId = '00000000-0000-0000-0000-000000000005';
    const ctrl = setup(userId);
    const f = TestBed.createComponent(MemberEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/capabilities/catalog').flush(catalog);
    ctrl.expectOne('http://localhost:5000/clients/C1/members').flush([
      { userId, roles: ['Admin'], capabilities: ['admin.users'] },
    ]);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.save();
    ctrl.expectOne(`http://localhost:5000/clients/C1/members/${userId}`)
      .flush({ detail: 'This would leave the client with no admin.users member.' }, { status: 409, statusText: 'Conflict' });
    f.detectChanges();
    expect(cmp.message()).toContain('no admin.users member');
  });
});
