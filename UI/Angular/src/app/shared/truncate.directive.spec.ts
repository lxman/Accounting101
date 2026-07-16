import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TruncateDirective } from './truncate.directive';

@Component({
  imports: [TruncateDirective],
  template: `<span appTruncate>hello world</span>`,
})
class Host {}

describe('TruncateDirective', () => {
  it('applies block truncate min-w-0 and sets no title', () => {
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideZonelessChangeDetection()],
    });
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const span: HTMLElement = f.nativeElement.querySelector('span');
    expect(span.classList.contains('block')).toBe(true);
    expect(span.classList.contains('truncate')).toBe(true);
    expect(span.classList.contains('min-w-0')).toBe(true);
    expect(span.hasAttribute('title')).toBe(false);
  });
});
