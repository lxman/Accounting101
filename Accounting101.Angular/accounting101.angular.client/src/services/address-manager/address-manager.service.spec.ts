import { TestBed } from '@angular/core/testing';

import { AddressManagerService } from './address-manager.service';

describe('AddressManagerService', () => {
  let service: AddressManagerService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(AddressManagerService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
