import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Paginator } from './paginator';

describe('Paginator', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] }));

  function make(currentPage: number, pageCount: number) {
    const f = TestBed.createComponent(Paginator);
    f.componentRef.setInput('currentPage', currentPage);
    f.componentRef.setInput('pageCount', pageCount);
    f.detectChanges();
    const previousEl = f.nativeElement.querySelector('hlm-pagination-previous a') as HTMLAnchorElement;
    const nextEl = f.nativeElement.querySelector('hlm-pagination-next a') as HTMLAnchorElement;
    return { f, previousEl, nextEl };
  }

  it('renders "Page X of Y"', () => {
    const { f } = make(2, 5);
    expect(f.nativeElement.textContent).toContain('Page 2 of 5');
  });

  it('disables the previous control when on the first page', () => {
    const { previousEl } = make(1, 5);
    expect(previousEl.className.split(' ')).toContain('pointer-events-none');
  });

  it('does not disable the previous control when past the first page', () => {
    const { previousEl } = make(2, 5);
    expect(previousEl.className.split(' ')).not.toContain('pointer-events-none');
  });

  it('disables the next control when on the last page', () => {
    const { nextEl } = make(5, 5);
    expect(nextEl.className.split(' ')).toContain('pointer-events-none');
  });

  it('emits previous when the previous control is clicked', () => {
    const { f, previousEl } = make(2, 5);
    let emitted = false;
    f.componentInstance.previous.subscribe(() => (emitted = true));
    previousEl.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(emitted).toBe(true);
  });

  it('emits next when the next control is clicked', () => {
    const { f, nextEl } = make(2, 5);
    let emitted = false;
    f.componentInstance.next.subscribe(() => (emitted = true));
    nextEl.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(emitted).toBe(true);
  });
});
