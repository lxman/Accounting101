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
