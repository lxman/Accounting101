import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NavStateService, locate } from './nav-state.service';

describe('locate', () => {
  it('finds a childless top-level item (no submenu to open)', () => {
    expect(locate('/journal')).toEqual({ section: 'General Ledger', parent: null });
  });

  it('opens a parent-with-children own submenu when on its own page', () => {
    expect(locate('/cash')).toEqual({ section: 'Subledgers', parent: '/cash' });
    expect(locate('/audit')).toEqual({ section: 'Assurance', parent: '/audit' });
  });

  it('finds a nested child and reports its parent', () => {
    expect(locate('/cash/reconciliation')).toEqual({ section: 'Subledgers', parent: '/cash' });
    expect(locate('/audit/verify')).toEqual({ section: 'Assurance', parent: '/audit' });
  });

  it('returns null for an unknown path', () => {
    expect(locate('/nope')).toBeNull();
  });
});

describe('NavStateService', () => {
  function make(): NavStateService {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    return TestBed.inject(NavStateService);
  }

  it('opens a section on toggle and closes it on second toggle', () => {
    const s = make();
    expect(s.isSectionOpen('Subledgers')).toBe(false);
    s.toggleSection('Subledgers');
    expect(s.isSectionOpen('Subledgers')).toBe(true);
    s.toggleSection('Subledgers');
    expect(s.isSectionOpen('Subledgers')).toBe(false);
  });

  it('is single-open: opening a section closes any other open section', () => {
    const s = make();
    s.toggleSection('Subledgers');
    s.toggleSection('Assurance');
    expect(s.isSectionOpen('Assurance')).toBe(true);
    expect(s.isSectionOpen('Subledgers')).toBe(false);
  });

  it('closing/switching a section clears the open submenu', () => {
    const s = make();
    s.toggleSection('Subledgers');
    s.toggleParent('/cash');
    expect(s.isParentOpen('/cash')).toBe(true);
    s.toggleSection('Assurance');
    expect(s.isParentOpen('/cash')).toBe(false);
  });

  it('toggles the whole sidebar collapsed and back', () => {
    const s = make();
    expect(s.sidebarCollapsed()).toBe(false);
    s.toggleSidebar();
    expect(s.sidebarCollapsed()).toBe(true);
    s.toggleSidebar();
    expect(s.sidebarCollapsed()).toBe(false);
  });

  it('submenu is single-open and reliably toggles closed', () => {
    const s = make();
    s.toggleSection('Assurance');
    s.toggleParent('/audit');
    expect(s.isParentOpen('/audit')).toBe(true);
    s.toggleParent('/reports');
    expect(s.isParentOpen('/reports')).toBe(true);
    expect(s.isParentOpen('/audit')).toBe(false);
    s.toggleParent('/reports');
    expect(s.isParentOpen('/reports')).toBe(false);
  });
});
