import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PostingBadge } from './posting-badge';

describe('PostingBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));
  it('renders Pending for PendingApproval', () => {
    const f = TestBed.createComponent(PostingBadge);
    f.componentRef.setInput('posting', 'PendingApproval'); f.detectChanges();
    expect(f.nativeElement.querySelector('[data-testid=badge-pending]')).toBeTruthy();
    expect(f.nativeElement.textContent).toContain('Pending');
  });
  it('renders Posted otherwise', () => {
    const f = TestBed.createComponent(PostingBadge);
    f.componentRef.setInput('posting', 'Posted'); f.detectChanges();
    expect(f.nativeElement.querySelector('[data-testid=badge-posted]')).toBeTruthy();
  });
});
