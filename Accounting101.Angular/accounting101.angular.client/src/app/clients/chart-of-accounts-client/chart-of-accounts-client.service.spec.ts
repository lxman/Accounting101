import { TestBed } from '@angular/core/testing';

import { ChartOfAccountsClient } from './chart-of-accounts-client.service';

describe('ChartOfAccountsService', () => {
  let service: ChartOfAccountsClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ChartOfAccountsClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
