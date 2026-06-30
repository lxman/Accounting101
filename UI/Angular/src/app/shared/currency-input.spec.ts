import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CurrencyInput } from './currency-input';

describe('CurrencyInput', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));

  function make(value = 0) {
    const f = TestBed.createComponent(CurrencyInput);
    f.componentRef.setInput('value', value);
    f.detectChanges();
    return { f, input: f.nativeElement.querySelector('input') as HTMLInputElement };
  }

  it('renders a $ adornment and a text input (no native number spinner)', () => {
    const { f, input } = make();
    expect(f.nativeElement.textContent).toContain('$');
    expect(input.getAttribute('type')).toBe('text');
    expect(input.getAttribute('inputmode')).toBe('decimal');
  });

  it('formats the bound value to 2 decimals', () => {
    const { input } = make(1250.5);
    expect(input.value).toBe('1250.50');
  });

  it('emits the parsed number on input (no premature reformat while typing)', () => {
    const { f, input } = make();
    let emitted: number | undefined;
    f.componentInstance.valueChange.subscribe((v) => (emitted = v));
    input.value = '1234.5';
    input.dispatchEvent(new Event('input'));
    expect(emitted).toBe(1234.5);
    expect(input.value).toBe('1234.5'); // not reformatted mid-type
  });

  it('reformats to 2 decimals on blur', () => {
    const { f, input } = make();
    input.dispatchEvent(new Event('focus'));
    input.value = '5';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    f.detectChanges();
    expect(input.value).toBe('5.00');
  });

  it('parses garbage to 0', () => {
    const { f, input } = make();
    let emitted: number | undefined;
    f.componentInstance.valueChange.subscribe((v) => (emitted = v));
    input.value = 'abc';
    input.dispatchEvent(new Event('input'));
    expect(emitted).toBe(0);
  });
});
