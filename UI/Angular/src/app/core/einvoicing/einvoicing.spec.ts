import { matchVendor, findDuplicates, profileLabel } from './einvoicing';
import { Vendor, Bill } from '../payables/payables';

const vendor = (id: string, name: string): Vendor => ({ id, name, email: null });

const bill = (id: string, vendorReference: string | null, status: Bill['status'] = 'Entered'): Bill => ({
  id, vendorId: 'v1', number: null, billDate: '2026-07-01', dueDate: null,
  vendorReference, memo: null, status, lines: [],
});

describe('matchVendor', () => {
  const vendors = [vendor('v1', 'Acme Parts'), vendor('v2', 'Globex')];

  it('suggests the single exact match', () => {
    expect(matchVendor('Acme Parts', vendors)?.id).toBe('v1');
  });

  it('ignores case and surrounding whitespace on both sides', () => {
    expect(matchVendor('  acme PARTS ', [vendor('v1', ' Acme Parts ')])?.id).toBe('v1');
  });

  it('returns null when nothing matches', () => {
    expect(matchVendor('Initech', vendors)).toBeNull();
  });

  it('stays silent when two vendors share the name', () => {
    expect(matchVendor('Acme Parts', [vendor('v1', 'Acme Parts'), vendor('v3', 'Acme Parts')])).toBeNull();
  });

  it('returns null for a null or blank seller name', () => {
    expect(matchVendor(null, vendors)).toBeNull();
    expect(matchVendor('   ', vendors)).toBeNull();
  });
});

describe('findDuplicates', () => {
  it('finds a bill with the same vendor reference', () => {
    const hits = findDuplicates('INV-9', [bill('b1', 'INV-9'), bill('b2', 'INV-8')]);
    expect(hits.map(b => b.id)).toEqual(['b1']);
  });

  it('ignores case and whitespace', () => {
    expect(findDuplicates(' inv-9 ', [bill('b1', 'INV-9')]).length).toBe(1);
  });

  it('excludes voided bills', () => {
    expect(findDuplicates('INV-9', [bill('b1', 'INV-9', 'Void')])).toEqual([]);
  });

  it('checks nothing when the document has no reference', () => {
    expect(findDuplicates(null, [bill('b1', 'INV-9')])).toEqual([]);
  });
});

describe('profileLabel', () => {
  it('labels a Factur-X profile enum name', () => {
    expect(profileLabel('En16931')).toBe('EN 16931');
    expect(profileLabel('BasicWl')).toBe('BASIC WL');
  });

  it('labels the UBL customisation URNs it recognises', () => {
    expect(profileLabel('urn:cen.eu:en16931:2017')).toBe('EN 16931');
    expect(profileLabel('urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_3.0'))
      .toBe('XRechnung');
    expect(profileLabel('urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0'))
      .toBe('Peppol BIS');
  });

  it('shows an unrecognised profile verbatim rather than as "Unknown"', () => {
    expect(profileLabel('urn:example:something:else')).toBe('urn:example:something:else');
  });

  it('reports an absent profile as unspecified', () => {
    expect(profileLabel(null)).toBe('Unspecified');
  });
});
