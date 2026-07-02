import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { canWrite } from './can.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('canWrite', () => {
  async function run(caps: string[]) {
    const stub = new StubCapabilityService();
    stub.set(caps);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: CapabilityService, useValue: stub }],
    });
    const injector = TestBed.inject(EnvironmentInjector);
    const guard = canWrite('ar.write', '/receivables/invoices');
    return runInInjectionContext(injector, () =>
      firstValueFrom(guard({} as any, {} as any) as any));
  }

  it('allows when the capability is held', async () => {
    expect(await run(['ar.write'])).toBe(true);
  });

  it('redirects to the fallback when the capability is absent', async () => {
    const result = await run(['ar.read']);
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/receivables/invoices');
  });
});
