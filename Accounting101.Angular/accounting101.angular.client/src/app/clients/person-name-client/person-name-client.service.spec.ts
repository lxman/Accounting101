import { TestBed } from '@angular/core/testing';

import { PersonNameClient } from './person-name-client.service';

describe('PersonNameService', () => {
  let service: PersonNameClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(PersonNameClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
