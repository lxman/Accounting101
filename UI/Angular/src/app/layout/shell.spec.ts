import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
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

  it('renders all five section headers', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    expect(el.textContent).toContain('Overview');
    expect(el.textContent).toContain('General Ledger');
    expect(el.textContent).toContain('Subledgers');
    expect(el.textContent).toContain('Assurance');
    expect(el.textContent).toContain('Administration');
  });

  it('starts with sections collapsed — items hidden until expanded', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    // No section holds the active route in the test harness (router url is '/'),
    // so every section is collapsed by default: headers show, items do not.
    expect(el.textContent).not.toContain('Posting accounts');
    expect(el.textContent).not.toContain('Trial Balance');

    const header = Array.from(el.querySelectorAll('[data-testid="nav-section-header"]'))
      .find((h) => h.textContent?.includes('Administration')) as HTMLElement;
    header.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Posting accounts');
  });

  it('shows Administration Firm/Client links after expanding (moved out of the header)', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    // The header buttons are gone regardless of expansion.
    expect(el.textContent).not.toContain('Edit Firm');
    expect(el.textContent).not.toContain('Edit Client');
    // The links live in the (collapsed) Administration section; expand to reveal.
    const header = Array.from(el.querySelectorAll('[data-testid="nav-section-header"]'))
      .find((h) => h.textContent?.includes('Administration')) as HTMLElement;
    header.click();
    fixture.detectChanges();
    const after = fixture.nativeElement as HTMLElement;
    expect(after.querySelector('a[href="/admin/firm"]')).not.toBeNull();
    expect(after.querySelector('a[href="/admin/client"]')).not.toBeNull();
  });

  it('nests Bank Reconciliation under Cash & Banking — hidden until both are expanded', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).not.toContain('Bank Reconciliation');
    const component = fixture.componentInstance as unknown as { toggle: (k: string) => void };
    component.toggle('Subledgers');       // open the section
    fixture.detectChanges();
    component.toggle('/cash');            // open the Cash & Banking parent
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Bank Reconciliation');
  });

  it('auto-opens the section holding the active route, without a manual toggle', async () => {
    // Drive the REAL url signal by navigating, so OnPush re-renders (a monkey-patched
    // activePath would not trigger change detection). Active route = Bank Reconciliation
    // under Subledgers; that section + parent open with no expand click, others stay shut.
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([{ path: 'cash/reconciliation', children: [] }])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    await TestBed.inject(Router).navigateByUrl('/cash/reconciliation');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const component = fixture.componentInstance as unknown as { isOpen: (s: string) => boolean };
    expect(component.isOpen('Subledgers')).toBe(true);
    expect(component.isOpen('Assurance')).toBe(false);
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
