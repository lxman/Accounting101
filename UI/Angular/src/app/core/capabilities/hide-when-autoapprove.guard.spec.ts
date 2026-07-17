import { TestBed } from '@angular/core/testing';
import { UrlTree, provideRouter } from '@angular/router';
import { runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { hideWhenAutoApproveGuard } from './hide-when-autoapprove.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('hideWhenAutoApproveGuard', () => {
  async function run(mode: 'TwoPerson' | 'SelfApprove' | 'AutoApprove') {
    const stub = new StubCapabilityService();
    stub.setApprovalMode(mode);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const route = { data: { fallback: '/journal' } } as any;
    return runInInjectionContext(injector, () =>
      firstValueFrom(hideWhenAutoApproveGuard(route, {} as any) as any));
  }

  it('allows the route when the client is on TwoPerson', async () => {
    expect(await run('TwoPerson')).toBe(true);
  });

  it('allows the route when the client is on SelfApprove', async () => {
    expect(await run('SelfApprove')).toBe(true);
  });

  it('redirects to the fallback when the client is on AutoApprove', async () => {
    const result = await run('AutoApprove');
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/journal');
  });

  it('waits until capabilities are loaded before emitting', async () => {
    const stub = new StubCapabilityService();
    stub.setApprovalMode('AutoApprove');
    stub.setLoaded(false);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const route = { data: { fallback: '/journal' } } as any;

    let resolved = false;
    const result$ = runInInjectionContext(injector, () => hideWhenAutoApproveGuard(route, {} as any) as any);
    const promise = firstValueFrom(result$).then((v: unknown) => { resolved = true; return v; });
    await Promise.resolve();
    await Promise.resolve();
    expect(resolved).toBe(false);

    stub.setLoaded(true);
    const result = await promise;
    expect(resolved).toBe(true);
    expect(result).toBeInstanceOf(UrlTree);
  });
});
