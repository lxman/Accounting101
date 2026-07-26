import { HttpErrorResponse } from '@angular/common/http';

export interface Problem { detail: string; fieldErrors: Record<string, string[]>; }

export function extractProblem(err: unknown): Problem {
  const body = err instanceof HttpErrorResponse ? err.error : (err as { error?: unknown })?.error ?? err;
  const fieldErrors = (body && typeof body === 'object' && 'errors' in body
    ? (body as { errors: Record<string, string[]> }).errors : {}) ?? {};
  const flat = Object.values(fieldErrors).flat();
  const detail = (body && typeof body === 'object' && 'detail' in body && (body as { detail?: string }).detail)
    || (flat.length ? flat.join('; ') : null)
    || (err instanceof HttpErrorResponse ? `Request failed (${err.status})` : 'Request failed');
  return { detail, fieldErrors };
}

/**
 * The ledger's answer when a retry key is reused for different financial content. Matching on the
 * engine's exact wording is a deliberate contract, pinned by a backend test
 * (Payables BillPaymentRetryKeyTests.The_same_key_with_different_content_is_a_422) so a reworded
 * engine message breaks the build rather than silently leaking jargon onto an operator's screen.
 *
 * Returns null for anything else, so callers fall through to extractProblem(err).detail.
 */
const DIVERGENT_RETRY_KEY = 'An entry with this id already exists with different content.';

export function retryKeyConflictMessage(err: unknown): string | null {
  return err instanceof HttpErrorResponse
    && err.status === 422
    && extractProblem(err).detail === DIVERGENT_RETRY_KEY
    ? 'This was already recorded, and differs from what you just submitted. Open the list and check it before retrying.'
    : null;
}
