import { Vendor, Bill } from '../payables/payables';

// ── Wire models ──────────────────────────────────────────────────────────────
// Mirrors of the DTOs in Accounting101.EInvoicing.Api/EInvoicingContracts.cs.

export type FindingSeverity = 'Error' | 'Warning' | 'Information';
export type InboundSyntax = 'Cii' | 'Ubl' | 'Unknown';

/** One validation finding. Official rule ids (BR-*, PEPPOL-*, BR-DE-*) pass through unchanged;
 *  module findings carry the `einvoicing-` prefix. Both are shown verbatim — they are what an
 *  operator takes to their vendor. */
export interface FindingDto {
  ruleId: string; severity: FindingSeverity; message: string;
  businessTerm: string | null; location: string | null;
}

export interface VendorMatchDto { vendorId: string | null; vendorName: string | null; sellerName: string | null; }

export interface ProposedLineDto { description: string; amount: number; }

/** `total` is always BT-112 (tax-inclusive), never BT-115. `linesAreDetail` is false when the
 *  backend could not reconcile detail lines and fell back to one line at the document total. */
export interface BillProposalDto {
  suggestedVendorId: string | null; sellerName: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: ProposedLineDto[];
  total: number; linesAreDetail: boolean;
}

export interface InboundPreviewResponse {
  syntax: InboundSyntax; profile: string | null; canImport: boolean;
  findings: FindingDto[]; proposal: BillProposalDto | null; vendorMatch: VendorMatchDto;
}

// ── Pure helpers ─────────────────────────────────────────────────────────────

/**
 * Suggests an existing vendor for a document's seller. A verbatim mirror of the backend's
 * `VendorMatcher.Match`: trim, case-insensitive equality, suggest only on a unique hit, stay
 * silent when two vendors share a name.
 *
 * The mirroring is deliberate. The endpoint always passes an empty vendor list (the module
 * references no other module, so it cannot read Payables' vendors), which leaves matching to this
 * screen — but the backend matcher is built and tested, and the two must not drift.
 */
export function matchVendor(sellerName: string | null | undefined, vendors: readonly Vendor[]): Vendor | null {
  if (!sellerName || !sellerName.trim()) return null;
  const needle = sellerName.trim().toLowerCase();
  const hits = vendors.filter(v => (v.name ?? '').trim().toLowerCase() === needle);
  return hits.length === 1 ? hits[0] : null;
}

/** Existing bills for the same vendor carrying the same vendor reference. Voided bills never
 *  count — they are exactly the case where re-entering the invoice is correct. */
export function findDuplicates(reference: string | null | undefined, bills: readonly Bill[]): Bill[] {
  if (!reference || !reference.trim()) return [];
  const needle = reference.trim().toLowerCase();
  return bills.filter(b => b.status !== 'Void' && (b.vendorReference ?? '').trim().toLowerCase() === needle);
}

const PROFILE_ENUM_LABELS: Record<string, string> = {
  Minimum: 'MINIMUM', BasicWl: 'BASIC WL', Basic: 'BASIC',
  En16931: 'EN 16931', Extended: 'EXTENDED', XRechnung: 'XRechnung', Peppol: 'Peppol BIS',
};

/**
 * A short label for the response's `profile`, which has two shapes: a FacturXProfile enum name for
 * CII, a raw cbc:CustomizationID URN for UBL. Never branches on syntax, and never renders an
 * unrecognised value as "Unknown" — an unfamiliar profile shows up honestly as itself.
 */
export function profileLabel(profile: string | null | undefined): string {
  if (!profile || !profile.trim()) return 'Unspecified';
  const p = profile.trim();
  const known = PROFILE_ENUM_LABELS[p];
  if (known) return known;
  const lower = p.toLowerCase();
  if (lower.includes('xrechnung')) return 'XRechnung';
  if (lower.includes('peppol.eu')) return 'Peppol BIS';
  if (lower === 'urn:cen.eu:en16931:2017') return 'EN 16931';
  return p;
}
