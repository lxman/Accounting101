import { TestBed } from '@angular/core/testing';
import { PaginationPrefsService } from './pagination-prefs.service';

describe('PaginationPrefsService', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('defaults to 50 when nothing is stored', () => {
    expect(TestBed.inject(PaginationPrefsService).pageSize()).toBe(50);
  });

  it('persists a chosen size to localStorage and exposes it', () => {
    const svc = TestBed.inject(PaginationPrefsService);
    svc.setPageSize(100);
    expect(svc.pageSize()).toBe(100);
    expect(localStorage.getItem('a101:rowsPerPage')).toBe('100');
  });

  it('restores a previously stored size on construction', () => {
    localStorage.setItem('a101:rowsPerPage', '25');
    expect(TestBed.inject(PaginationPrefsService).pageSize()).toBe(25);
  });

  it('falls back to the default on a malformed stored value', () => {
    localStorage.setItem('a101:rowsPerPage', 'nonsense');
    expect(TestBed.inject(PaginationPrefsService).pageSize()).toBe(50);
  });

  it('ignores non-positive sizes', () => {
    const svc = TestBed.inject(PaginationPrefsService);
    svc.setPageSize(0);
    expect(svc.pageSize()).toBe(50);
  });
});
