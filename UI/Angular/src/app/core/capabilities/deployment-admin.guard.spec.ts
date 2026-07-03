import { TestBed } from '@angular/core/testing';
import { UrlTree, provideRouter } from '@angular/router';
import { runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { deploymentAdminGuard } from './deployment-admin.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('deploymentAdminGuard', () => {
  async function run(deploymentAdmin: boolean) {
    const stub = new StubCapabilityService();
    stub.setDeploymentAdmin(deploymentAdmin);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const guard = deploymentAdminGuard('/admin/users');
    return runInInjectionContext(injector, () =>
      firstValueFrom(guard({} as any, {} as any) as any));
  }

  it('allows when the user is a deployment admin', async () => {
    expect(await run(true)).toBe(true);
  });

  it('redirects to the fallback when the user is not a deployment admin', async () => {
    const result = await run(false);
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/admin/users');
  });

  it('waits until capabilities are loaded before emitting', async () => {
    const stub = new StubCapabilityService();
    stub.setDeploymentAdmin(true);
    stub.setLoaded(false);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const guard = deploymentAdminGuard('/admin/users');

    let resolved = false;
    const result$ = runInInjectionContext(injector, () => guard({} as any, {} as any) as any);
    const promise = firstValueFrom(result$).then((v) => { resolved = true; return v; });

    // Let any pending microtasks flush; the guard must not have emitted yet since loaded=false.
    await Promise.resolve();
    await Promise.resolve();
    expect(resolved).toBe(false);

    stub.setLoaded(true);
    const result = await promise;
    expect(resolved).toBe(true);
    expect(result).toBe(true);
  });
});
