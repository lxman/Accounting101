import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Shell } from './shell';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { environment } from '../core/api/environment';

describe('Shell', () => {
  async function make() {
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('renders section headers and Dashboard', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('General Ledger');
    expect(el.textContent).toContain('Subledgers');
    expect(el.textContent).toContain('Administration');
    expect(el.textContent).toContain('Dashboard');
  });

  it('shows Administration Firm/Client links (moved out of the header)', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Firm');
    expect(el.textContent).toContain('Client');
    expect(el.textContent).not.toContain('Edit Firm');
    expect(el.textContent).not.toContain('Edit Client');
  });

  it('shows a nested child under its parent (default expanded)', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Bank Reconciliation');
  });

  it('collapsing a section hides its items', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    // find the Administration section header toggle and click it
    const header = Array.from(el.querySelectorAll('[data-testid="nav-section-header"]'))
      .find((h) => h.textContent?.includes('Administration')) as HTMLElement;
    header.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Posting accounts');
  });

  it('keeps a collapsed section auto-expanded when it holds the active route', async () => {
    const fixture = await make();
    const component = fixture.componentInstance as unknown as {
      activePath: () => string | null;
      toggle: (key: string) => void;
      isOpen: (sectionLabel: string) => boolean;
    };
    // Force the active path onto the Bank Reconciliation child under Subledgers,
    // then collapse the Subledgers section — it should stay open because it
    // contains the active route.
    Object.defineProperty(component, 'activePath', { value: () => '/cash/reconciliation' });
    component.toggle('Subledgers');
    fixture.detectChanges();
    expect(component.isOpen('Subledgers')).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Bank Reconciliation');
  });

  it('switches the active dev identity from the top bar', async () => {
    const fixture = await make();
    const ids = TestBed.inject(DevIdentityService);
    ids.use(environment.devApprover.sub);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Dev Approver');
  });
});
