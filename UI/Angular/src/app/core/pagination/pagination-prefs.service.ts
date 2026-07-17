import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'a101:rowsPerPage';
const DEFAULT_PAGE_SIZE = 50;

/**
 * Persists the user's "rows per page" choice across navigation and reloads.
 * App-wide (a single preference shared by every list), backed by localStorage
 * with a self-healing read that falls back to the default on a missing or
 * malformed value.
 */
@Injectable({ providedIn: 'root' })
export class PaginationPrefsService {
  private readonly _pageSize = signal(this.read());
  readonly pageSize = this._pageSize.asReadonly();

  setPageSize(size: number): void {
    if (!Number.isFinite(size) || size <= 0) return;
    this._pageSize.set(size);
    try {
      localStorage.setItem(STORAGE_KEY, String(size));
    } catch {
      // Storage unavailable (private mode / quota) — keep the in-memory value.
    }
  }

  private read(): number {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (raw === null) return DEFAULT_PAGE_SIZE;
      const parsed = Number(raw);
      return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_PAGE_SIZE;
    } catch {
      return DEFAULT_PAGE_SIZE;
    }
  }
}
