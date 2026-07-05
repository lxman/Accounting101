export type DepreciationMethod = 'StraightLine' | 'DecliningBalance';
export type AssetStatus = 'Active' | 'Disposed';
export type DepreciationRunStatus = 'Posted' | 'Voided';
export type DisposalStatus = 'Posted' | 'Voided';

export const methodLabel = (m: DepreciationMethod): string =>
  m === 'DecliningBalance' ? 'Declining balance' : 'Straight line';

export interface Asset {
  id: string;
  description: string;
  acquisitionCost: number;
  inServiceDate: string;
  usefulLifeMonths: number;
  salvageValue: number;
  method: DepreciationMethod;
  decliningBalanceFactor: number | null;
  status: AssetStatus;
  accumulatedDepreciation: number;
}
export interface AssetView { asset: Asset; netBookValue: number; }

export interface DepreciationRunLine { assetId: string; amount: number; }
export interface DepreciationPeriod { year: number; month: number; }
export interface DepreciationRun {
  id: string;
  number: string | null;
  period: DepreciationPeriod;
  effectiveDate: string;
  memo: string | null;
  lines: DepreciationRunLine[];
  total: number;
  status: DepreciationRunStatus;
}
export interface DepreciationRunView { run: DepreciationRun; }

export interface Disposal {
  id: string;
  number: string | null;
  assetId: string;
  disposalDate: string;
  proceeds: number;
  catchUpDepreciation: number;
  accumulatedBeforeDisposal: number;
  accumulatedAtDisposal: number;
  netBookValue: number;
  gainLoss: number;
  memo: string | null;
  status: DisposalStatus;
}
export interface DisposalView { disposal: Disposal; }

export interface SaveAssetRequest {
  description: string;
  acquisitionCost: number;
  inServiceDate: string;
  usefulLifeMonths: number;
  salvageValue: number;
  method: DepreciationMethod;
  decliningBalanceFactor: number | null;
}
export interface RunDepreciationRequest { year: number; month: number; effectiveDate?: string | null; memo?: string | null; }
export interface DisposeAssetRequest { disposalDate: string; proceeds: number; memo?: string | null; }

export interface FixedAssetsListQuery {
  skip: number;
  limit: number;
  order?: 'asc' | 'desc';
  includeInactive?: boolean;
  includeVoided?: boolean;
}
