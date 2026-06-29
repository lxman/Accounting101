import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { EntryList } from './entry-list';
import { EntriesService } from '../../core/entries/entries.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { PagedResponse } from '../../core/api/paged-response';
import { EntryResponse } from '../../core/entries/entry';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatProfileDate } from '../../core/format/date-formatter';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

function makeEntry(overrides: Partial<EntryResponse> = {}): EntryResponse {
  return {
    id: 'e1',
    sequenceNumber: 1,
    effectiveDate: '2025-03-15',
    type: 'Journal',
    status: 'Approved',
    posting: 'Posted',
    lineCount: 2,
    supersedes: null,
    supersededBy: null,
    reversalOf: null,
    reversedBy: null,
    lines: [],
    sourceRef: null,
    sourceType: null,
    reference: null,
    memo: 'Opening entry',
    viaModule: null,
    ...overrides,
  };
}

// 3 entries total, limit 2 → 2 pages
const mockPage: PagedResponse<EntryResponse> = {
  items: [
    makeEntry({ id: 'e1', sequenceNumber: 1, posting: 'Posted', memo: 'First entry' }),
    makeEntry({ id: 'e2', sequenceNumber: 2, posting: 'PendingApproval', memo: 'Second entry' }),
    makeEntry({ id: 'e3', sequenceNumber: 3, posting: 'Posted', memo: 'Third entry' }),
  ],
  total: 3,
  skip: 0,
  limit: 2,
};

describe('EntryList', () => {
  let stub: { listPaged: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    stub = { listPaged: vi.fn().mockReturnValue(of(mockPage)) };

    await TestBed.configureTestingModule({
      imports: [EntryList],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: EntriesService, useValue: stub },
      ],
    }).compileComponents();

    const clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);
  });

  it('renders a row for each entry in the page', async () => {
    const fixture = TestBed.createComponent(EntryList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const rows = el.querySelectorAll('tbody tr');
    expect(rows.length).toBe(3);
  });

  it('formats effective date via the formatter', async () => {
    const fixture = TestBed.createComponent(EntryList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const expected = formatProfileDate('2025-03-15', DEFAULT_FORMAT_PROFILE);
    expect(el.textContent).toContain(expected);
  });

  it('renders the Pending badge for PendingApproval entries', async () => {
    const fixture = TestBed.createComponent(EntryList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const pendingBadge = el.querySelector('[data-testid="badge-pending"]');
    expect(pendingBadge).not.toBeNull();
    expect(pendingBadge?.textContent?.trim()).toBe('Pending');
  });

  it('shows "Page 1 of 2" when total=3 and limit=2', async () => {
    const fixture = TestBed.createComponent(EntryList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Page 1 of 2');
  });

  it('re-queries when posting signal changes', async () => {
    const fixture = TestBed.createComponent(EntryList);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const callsBefore = (stub.listPaged as ReturnType<typeof vi.fn>).mock.calls.length;

    fixture.componentInstance.posting.set('Posted');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect((stub.listPaged as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(callsBefore);
    const lastCall = (stub.listPaged as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[0];
    expect(lastCall).toMatchObject({ posting: 'Posted', skip: 0 });
  });
});
