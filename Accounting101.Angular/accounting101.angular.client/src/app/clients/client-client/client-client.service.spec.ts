import { TestBed } from '@angular/core/testing';

import { ClientClient } from './client-client.service';

describe('ClientManagerService', () => {
  let service: ClientClient;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ClientClient);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
