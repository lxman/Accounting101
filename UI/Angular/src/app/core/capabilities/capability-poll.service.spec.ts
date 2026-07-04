import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CapabilityPollService, POLL_INTERVAL_MS } from './capability-poll.service';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';
import { ClientContextService } from '../client/client-context.service';

describe('CapabilityPollService', () => {
  let caps: StubCapabilityService;
  let client: ClientContextService;

  beforeEach(() => {
    vi.useFakeTimers();
    caps = new StubCapabilityService();
    vi.spyOn(caps, 'reload');
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CapabilityService, useValue: caps },
        ClientContextService,
      ],
    });
    client = TestBed.inject(ClientContextService);
  });
  afterEach(() => { vi.useRealTimers(); });

  it('reloads on each interval while a client is selected and the tab is visible', () => {
    client.select('c1');
    TestBed.inject(CapabilityPollService);      // starts the interval subscription
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).toHaveBeenCalledTimes(2);
  });

  it('does not reload while no client is selected', () => {
    client.select(null);
    TestBed.inject(CapabilityPollService);
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).not.toHaveBeenCalled();
  });

  it('does not reload while the tab is hidden', () => {
    client.select('c1');
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    TestBed.inject(CapabilityPollService);
    vi.advanceTimersByTime(POLL_INTERVAL_MS * 2);
    expect(caps.reload).not.toHaveBeenCalled();
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
  });
});
