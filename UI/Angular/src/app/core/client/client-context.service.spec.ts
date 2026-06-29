import { TestBed } from '@angular/core/testing';
import { ClientContextService } from './client-context.service';

describe('ClientContextService', () => {
  let service: ClientContextService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ClientContextService);
  });

  it('starts with null clientId', () => {
    expect(service.clientId()).toBeNull();
  });

  it('select() updates clientId signal', () => {
    service.select('client-42');
    expect(service.clientId()).toBe('client-42');
  });

  it('select(null) clears the clientId signal', () => {
    service.select('client-42');
    service.select(null);
    expect(service.clientId()).toBeNull();
  });
});
