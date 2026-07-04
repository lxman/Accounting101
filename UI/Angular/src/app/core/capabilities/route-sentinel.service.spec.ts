import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter, Router } from '@angular/router';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { RouteSentinelService } from './route-sentinel.service';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({ standalone: true, template: 'editor' }) class Editor {}
@Component({ standalone: true, template: 'list' }) class List {}

function setup() {
  const caps = new StubCapabilityService();
  caps.setLoaded(true);
  caps.set(['ar.write']);
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CapabilityService, useValue: caps },
      provideRouter([
        { path: 'list', component: List },
        { path: 'edit', component: Editor, data: { requiredCapability: 'ar.write', fallback: '/list' } },
      ]),
    ],
  });
  return caps;
}

describe('RouteSentinelService', () => {
  it('stays put while the required capability is held', async () => {
    setup();
    TestBed.inject(RouteSentinelService);              // start the sentinel
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    TestBed.flushEffects?.();
    expect(TestBed.inject(Router).url).toBe('/edit');
  });

  it('redirects off the page the moment the required capability disappears', async () => {
    const caps = setup();
    TestBed.inject(RouteSentinelService);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    TestBed.flushEffects?.();
    expect(TestBed.inject(Router).url).toBe('/edit');

    caps.set([]);                                       // capability revoked (empty set)
    TestBed.flushEffects?.();
    // Poll the URL until it flips (navigation is async):
    for (let i = 0; i < 20 && TestBed.inject(Router).url !== '/list'; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      TestBed.flushEffects?.();
    }
    expect(TestBed.inject(Router).url).toBe('/list');
  });
});
