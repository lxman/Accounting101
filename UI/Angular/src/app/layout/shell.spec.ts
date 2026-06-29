import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Shell } from './shell';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { environment } from '../core/api/environment';

describe('Shell', () => {
  it('renders the nav and top-bar actions', async () => {
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Dashboard');
    expect(el.textContent).toContain('Edit Firm');
    expect(el.textContent).toContain('Edit Client');
  });

  it('switches the active dev identity from the top bar', () => {
    TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([])],
    });
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    const ids = TestBed.inject(DevIdentityService);
    ids.use(environment.devApprover.sub);
    fixture.detectChanges();
    // The dropdown options live in a deferred overlay (via *hlmSelectPortal), so they are NOT in the
    // inline DOM until opened. The active identity is reflected in the trigger — assert the switch
    // took effect by the rendered active sub. (Human-readable label in the trigger is tracked separately.)
    expect(fixture.nativeElement.textContent).toContain(environment.devApprover.sub);
  });
});
