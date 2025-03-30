import { TestBed } from '@angular/core/testing';

import { BusinessClient } from './business-client.service';

describe('BusinessManagerService', () => {
  let service: BusinessClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(BusinessClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
