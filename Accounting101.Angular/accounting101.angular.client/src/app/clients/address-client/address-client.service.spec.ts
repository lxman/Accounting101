import { TestBed } from '@angular/core/testing';

import { AddressClient } from './address-client.service';

describe('AddressManagerService', () => {
  let service: AddressClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(AddressClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
