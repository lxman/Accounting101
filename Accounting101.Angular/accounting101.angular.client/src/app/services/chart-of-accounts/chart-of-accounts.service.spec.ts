import { TestBed } from '@angular/core/testing';

import { ChartOfAccountsService } from './chart-of-accounts.service';

describe('ChartOfAccountsService', () => {
  let service: ChartOfAccountsService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ChartOfAccountsService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
