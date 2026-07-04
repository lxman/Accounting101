import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter, Router } from '@angular/router';
import { Component } from '@angular/core';
import { provideZonelessChangeDetection } from '@angular/core';
import { canWrite } from './can.guard';
import { provideCapabilities } from './capability.testing';

@Component({ standalone: true, template: 'editor' }) class Editor {}
@Component({ standalone: true, template: 'list' }) class List {}

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideCapabilities(...caps),
      provideRouter([
        { path: 'list', component: List },
        { path: 'edit', component: Editor, canActivate: [canWrite],
          data: { requiredCapability: 'ar.write', fallback: '/list' } },
      ]),
    ],
  });
}

describe('canWrite (data-driven)', () => {
  it('allows navigation when the caller holds the required capability', async () => {
    setup(['ar.write']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    expect(TestBed.inject(Router).url).toBe('/edit');
  });

  it('redirects to the route data fallback when the capability is missing', async () => {
    setup(['ar.read']);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/edit');
    expect(TestBed.inject(Router).url).toBe('/list');
  });
});
