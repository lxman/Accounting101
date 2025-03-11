import { TestBed } from '@angular/core/testing';

import { AccountsManagerService } from './accounts-manager.service';

describe('AccountsManagerService', () => {
  let service: AccountsManagerService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(AccountsManagerService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
