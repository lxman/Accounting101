import { TestBed } from '@angular/core/testing';

import { PersonNameManagerService } from './person-name-manager.service';

describe('PersonNameService', () => {
  let service: PersonNameManagerService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(PersonNameManagerService);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
