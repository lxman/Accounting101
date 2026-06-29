import { extractProblem } from './problem-details';
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
