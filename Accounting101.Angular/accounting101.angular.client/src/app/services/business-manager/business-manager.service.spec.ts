import { TestBed } from '@angular/core/testing';

import { BusinessManagerService } from './business-manager.service';

describe('BusinessManagerService', () => {
  let service: BusinessManagerService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(BusinessManagerService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
