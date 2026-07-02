import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanDirective } from './can.directive';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

@Component({
  standalone: true,
  imports: [CanDirective],
  template: `<button *appCan="'ar.write'">New</button>`,
})
class Host {}

describe('CanDirective', () => {
  function make(caps: string[]) {
    const stub = new StubCapabilityService();
    stub.set(caps);
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: CapabilityService, useValue: stub }],
    });
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    return { f, stub };
  }

  it('renders the control when the capability is held', () => {
    const { f } = make(['ar.write']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).not.toBeNull();
  });

  it('removes the control when the capability is absent', () => {
    const { f } = make(['ar.read']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('reacts to a capability change', () => {
    const { f, stub } = make(['ar.read']);
    stub.set(['ar.write']);
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('button')).not.toBeNull();
  });
});
