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

  function sectionHeader(el: HTMLElement, label: string): HTMLElement {
    return Array.from(el.querySelectorAll('[data-testid="nav-section-header"]'))
      .find((h) => h.textContent?.includes(label)) as HTMLElement;
  }

  it('renders all five section headers', async () => {
    const el = (await make()).nativeElement as HTMLElement;
    for (const label of ['Overview', 'General Ledger', 'Subledgers', 'Assurance', 'Administration']) {
      expect(el.textContent).toContain(label);
    }
  });

  it('starts with sections collapsed — items hidden until expanded', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    // No section holds the active route in the harness (router url is '/'), so all collapsed.
    expect(el.textContent).not.toContain('Posting accounts');
    expect(el.textContent).not.toContain('Trial Balance');

    sectionHeader(el, 'Administration').click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Posting accounts');
  });

  it('shows Administration Firm/Client links after expanding (moved out of the header)', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).not.toContain('Edit Firm');
    expect(el.textContent).not.toContain('Edit Client');

    sectionHeader(el, 'Administration').click();
    fixture.detectChanges();
    const after = fixture.nativeElement as HTMLElement;
    expect(after.querySelector('a[href="/admin/firm"]')).not.toBeNull();
    expect(after.querySelector('a[href="/admin/client"]')).not.toBeNull();
  });

  it('is single-open: expanding one section collapses the previously open one', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    sectionHeader(el, 'Administration').click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Posting accounts');

    sectionHeader(fixture.nativeElement, 'General Ledger').click();
    fixture.detectChanges();
    const after = fixture.nativeElement as HTMLElement;
    expect(after.textContent).toContain('Trial Balance');       // General Ledger now open
    expect(after.textContent).not.toContain('Posting accounts'); // Administration collapsed
  });

  it('nests Bank Reconciliation under Cash & Banking and reliably toggles it closed again', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).not.toContain('Bank Reconciliation');

    sectionHeader(el, 'Subledgers').click();          // open the section
    fixture.detectChanges();
    const parentToggle = fixture.nativeElement.querySelector('[data-testid="nav-parent-toggle"]') as HTMLElement;
    parentToggle.click();                             // open Cash & Banking submenu
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Bank Reconciliation');

    (fixture.nativeElement.querySelector('[data-testid="nav-parent-toggle"]') as HTMLElement).click(); // close it
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Bank Reconciliation');
  });

  it('auto-opens the section and submenu holding the active route', async () => {
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter([{ path: 'cash/reconciliation', children: [] }])],
    }).compileComponents();
    const fixture = TestBed.createComponent(Shell);
    await TestBed.inject(Router).navigateByUrl('/cash/reconciliation');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Bank Reconciliation'); // Subledgers + Cash & Banking auto-open
    expect(el.textContent).not.toContain('Posting accounts'); // unrelated section stays closed
  });

  it('collapses and restores the entire sidebar from the header toggle', async () => {
    const fixture = await make();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('aside')).not.toBeNull();
    expect(el.textContent).toContain('General Ledger');

    const toggle = el.querySelector('[data-testid="sidebar-toggle"]') as HTMLElement;
    toggle.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('aside')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('General Ledger');

    toggle.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('aside')).not.toBeNull();
  });

  it('switches the active dev identity from the top bar', async () => {
    const fixture = await make();
    const ids = TestBed.inject(DevIdentityService);
    ids.use(environment.devApprover.sub);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Dev Approver');
  });
});
