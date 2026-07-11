import { CHART_HEALTH_MODULES } from './chart-health';

describe('CHART_HEALTH_MODULES readCap', () => {
  const expected: Record<string, string> = {
    receivables: 'ar.read',
    payables: 'ap.read',
    payroll: 'payroll.read',
    cash: 'cash.read',
    fixedassets: 'fixedassets.read',
    inventory: 'inventory.read',
  };

  it('maps every module to its {area}.read capability', () => {
    expect(CHART_HEALTH_MODULES.length).toBe(Object.keys(expected).length);
    for (const m of CHART_HEALTH_MODULES) {
      expect(m.readCap).toBe(expected[m.key]);
    }
  });
});
