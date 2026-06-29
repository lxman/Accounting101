import { TestBed } from '@angular/core/testing';
import { DevIdentityService } from './dev-identity.service';
import { environment } from './environment';

describe('DevIdentityService', () => {
  let svc: DevIdentityService;
  beforeEach(() => { TestBed.configureTestingModule({}); svc = TestBed.inject(DevIdentityService); });

  it('defaults to the clerk identity', () => {
    expect(svc.active().sub).toBe(environment.devClerk.sub);
    expect(svc.active().name).toBe('Dev Clerk');
  });

  it('lists both identities', () => {
    expect(svc.identities.map(i => i.sub)).toEqual([environment.devClerk.sub, environment.devApprover.sub]);
  });

  it('use(sub) switches the active identity', () => {
    svc.use(environment.devApprover.sub);
    expect(svc.active().sub).toBe(environment.devApprover.sub);
  });

  it('use(unknown) is a no-op', () => {
    svc.use('nope');
    expect(svc.active().sub).toBe(environment.devClerk.sub);
  });
});
