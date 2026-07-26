import { extractProblem, retryKeyConflictMessage } from './problem-details';
import { HttpErrorResponse } from '@angular/common/http';

describe('extractProblem', () => {
  it('reads ProblemDetails.detail', () => {
    const err = new HttpErrorResponse({ status: 409, error: { detail: 'Period is closed' } });
    expect(extractProblem(err).detail).toBe('Period is closed');
  });
  it('flattens ValidationProblemDetails.errors', () => {
    const err = new HttpErrorResponse({ status: 422, error: { errors: { 'lines[0].amount': ['must be > 0'] } } });
    const p = extractProblem(err);
    expect(p.fieldErrors['lines[0].amount']).toEqual(['must be > 0']);
    expect(p.detail).toContain('must be > 0');
  });
  it('falls back to a generic message', () => {
    expect(extractProblem(new HttpErrorResponse({ status: 500 })).detail).toBeTruthy();
  });
});

describe('retryKeyConflictMessage', () => {
  it('recognises the ledger\'s divergent-retry-key 422', () => {
    const err = new HttpErrorResponse({
      status: 422,
      error: { detail: 'An entry with this id already exists with different content.' },
    });

    expect(retryKeyConflictMessage(err))
      .toBe('This was already recorded, and differs from what you just submitted. Open the list and check it before retrying.');
  });

  it('returns null for an ordinary 422 so the caller shows the real message', () => {
    const err = new HttpErrorResponse({
      status: 422,
      error: { detail: 'A payment amount must be greater than zero.' },
    });

    expect(retryKeyConflictMessage(err)).toBeNull();
  });

  it('returns null for a non-422', () => {
    const err = new HttpErrorResponse({ status: 409, error: { detail: 'Period is closed.' } });
    expect(retryKeyConflictMessage(err)).toBeNull();
  });
});
