export type AccountReadinessStatus = 'Ok' | 'Missing' | 'Inactive' | 'WrongType' | 'MissingDimensions';

export interface AccountReadinessResult {
  accountId: string;
  label: string;
  expectedType: string | null;
  requiredDimensions: string[];
  status: AccountReadinessStatus;
  actualType: string | null;
  actualRequiredDimensions: string[] | null;
  detail: string;
}

export interface ChartReadinessReport {
  moduleKey: string;
  ready: boolean;
  accounts: AccountReadinessResult[];
}

/** One module's readiness for the widget; `report` is null when the host call failed. */
export interface ModuleHealth {
  key: string;
  label: string;
  report: ChartReadinessReport | null;
  errored: boolean;
}

/** The six modules the widget checks, in display order. */
export const CHART_HEALTH_MODULES: { key: string; label: string }[] = [
  { key: 'receivables', label: 'Receivables' },
  { key: 'payables', label: 'Payables' },
  { key: 'payroll', label: 'Payroll' },
  { key: 'cash', label: 'Cash' },
  { key: 'fixedassets', label: 'Fixed Assets' },
  { key: 'inventory', label: 'Inventory' },
];
