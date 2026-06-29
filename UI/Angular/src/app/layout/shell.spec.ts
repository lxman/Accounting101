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
    expect(fixture.nativeElement.textContent).toContain('Dev Approver');
  });
});
