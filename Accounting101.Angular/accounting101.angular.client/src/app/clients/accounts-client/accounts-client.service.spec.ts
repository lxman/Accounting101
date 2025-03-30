import { TestBed } from '@angular/core/testing';

import { AccountsClient } from './accounts-client.service';

describe('AccountsManagerService', () => {
  let service: AccountsClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(AccountsClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
