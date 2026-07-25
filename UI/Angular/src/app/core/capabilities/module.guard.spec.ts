import { TestBed } from '@angular/core/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { moduleEnabled } from './module.guard';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({ standalone: true, template: 'import' }) class Importer {}
@Component({ standalone: true, template: 'bills' }) class Bills {}

function setup(modules: string[], loaded = true) {
  const caps = new StubCapabilityService();
  caps.setLoaded(loaded);
  caps.setEnabledModules(modules);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CapabilityService, useValue: caps },
      provideRouter([
        { path: 'bills', component: Bills },
        { path: 'import', component: Importer, canActivate: [moduleEnabled],
          data: { requiredModule: 'einvoicing', fallback: '/bills' } },
      ]),
    ],
  });
  return caps;
}

describe('moduleEnabled guard', () => {
  it('allows navigation when the module is enabled', async () => {
    setup(['einvoicing']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/import');
  });

  it('redirects to the fallback when the module is not enabled', async () => {
    setup([]);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/bills');
  });

  it('waits for capabilities to load before deciding', async () => {
    const caps = setup(['einvoicing'], false);
    const harness = await RouterTestingHarness.create();
    const nav = harness.navigateByUrl('/import');
    expect(TestBed.inject(Router).url).toBe('/');   // still pending — the guard has not resolved
    caps.setLoaded(true);
    await nav;
    expect(TestBed.inject(Router).url).toBe('/import');
  });
});
