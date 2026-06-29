import { Directive, computed, input } from '@angular/core';
import { BrnButton } from '@spartan-ng/brain/button';
import { cva, type VariantProps } from 'class-variance-authority';

export const buttonVariants = cva(
  'inline-flex shrink-0 items-center justify-center whitespace-nowrap rounded-md text-sm font-medium transition-all outline-none select-none disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        default: 'bg-primary text-primary-foreground shadow-xs hover:bg-primary/90',
        outline:
          'border border-border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground',
        secondary: 'bg-secondary text-secondary-foreground shadow-xs hover:bg-secondary/80',
        ghost: 'hover:bg-accent hover:text-accent-foreground',
        destructive: 'bg-destructive text-destructive-foreground shadow-xs hover:bg-destructive/90',
        link: 'text-primary underline-offset-4 hover:underline',
      },
      size: {
        default: 'h-9 px-4 py-2',
        xs: 'h-7 rounded px-2 text-xs',
        sm: 'h-8 rounded-md px-3 text-xs',
        lg: 'h-10 rounded-md px-8',
        icon: 'h-9 w-9',
      },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
);

export type ButtonVariants = VariantProps<typeof buttonVariants>;

@Directive({
  selector: 'button[hlmBtn], a[hlmBtn]',
  exportAs: 'hlmBtn',
  hostDirectives: [{ directive: BrnButton, inputs: ['disabled'] }],
  host: {
    'data-slot': 'button',
    '[class]': '_classes()',
  },
})
export class HlmButton {
  readonly variant = input<ButtonVariants['variant']>('default');
  readonly size = input<ButtonVariants['size']>('default');

  protected readonly _classes = computed(() =>
    buttonVariants({ variant: this.variant(), size: this.size() }),
  );
}
