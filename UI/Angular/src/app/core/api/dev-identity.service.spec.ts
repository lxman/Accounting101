import { TestBed } from '@angular/core/testing';
import { DevIdentityService } from './dev-identity.service';

describe('DevIdentityService', () => {
  it('offers the five dev roles and starts on the first', () => {
    const svc = TestBed.inject(DevIdentityService);
    expect(svc.identities.map(i => i.name)).toEqual([
      'Dev Controller', 'Dev Approver', 'Dev Auditor', 'Dev AR Clerk', 'Dev Admin',
    ]);
    expect(svc.active()).toBe(svc.identities[0]);
  });

  it('switches the active identity by sub', () => {
    const svc = TestBed.inject(DevIdentityService);
    svc.use(svc.identities[3].sub);
    expect(svc.active().name).toBe('Dev AR Clerk');
  });
});
