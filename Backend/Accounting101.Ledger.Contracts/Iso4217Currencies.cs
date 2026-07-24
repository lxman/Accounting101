namespace Accounting101.Ledger.Contracts;

/// <summary>An ISO 4217 currency: the 3-letter code, a display symbol (the code itself where there is
/// no well-known glyph), and the English name.</summary>
public sealed record CurrencyInfo(string Code, string Symbol, string Name);

/// <summary>The active ISO 4217 circulating currencies — real national currencies only; the X-codes
/// (no-currency XXX, testing XTS, precious metals XAU/XAG/XPT/XPD, fund/bond codes) are deliberately
/// excluded. The intended single source of truth for the base-currency allowlist and the
/// display-symbol catalog — the one committed exception is a temporary hand-maintained mirror of
/// <see cref="Symbols"/> in the frontend's money-formatter.ts (see the note there), kept only until
/// that UI slice seeds itself from GET /currencies/catalog instead.
/// To add a currency, add a row to <see cref="All"/> (and a glyph to Symbols if it has a well-known
/// one); tests assert behavior on a sample, not an exact count, so extension is safe.
///
/// <para><b>Removal is a one-way door.</b> BaseCurrency is validated only at provisioning and is
/// immutable thereafter, so removing a code from the active table makes it permanently unselectable
/// for future provisioning with no operator override — the bar for removing a row is higher than for
/// adding one. It cannot, however, break an already-provisioned client: nothing re-validates a
/// stored BaseCurrency on read, and <see cref="Find"/> returning null degrades to code-as-symbol
/// display.</para>
/// </summary>
public static class Iso4217Currencies
{
    // Well-known single-glyph symbols. Currencies not listed here fall back to their code as the symbol.
    //
    // Temporary frontend mirror: UI/Angular/src/app/core/format/money-formatter.ts's SYMBOLS map
    // duplicates this table by hand — the frontend has no wiring yet to consume GET /currencies/catalog
    // at runtime (a deferred UI slice). Keep the two in sync until that slice lands and the mirror
    // is retired.
    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["CNY"] = "¥",
        ["INR"] = "₹", ["KRW"] = "₩", ["RUB"] = "₽", ["BRL"] = "R$", ["ZAR"] = "R",
        ["TRY"] = "₺", ["ILS"] = "₪", ["THB"] = "฿", ["PHP"] = "₱", ["VND"] = "₫",
        ["NGN"] = "₦", ["UAH"] = "₴", ["PLN"] = "zł", ["CZK"] = "Kč", ["AUD"] = "$",
        ["CAD"] = "$", ["NZD"] = "$", ["HKD"] = "$", ["SGD"] = "$", ["MXN"] = "$",
    };

    // (Code, English name). Active circulating currencies; extend as needed.
    //
    // Fix-round-1 audit (2026-07-24) against the current ISO 4217 active-currency register added 9
    // real circulating currencies the original transcription omitted, and removed 3 codes that have
    // since been superseded and are no longer circulating (verified against ISO 4217 amendment notes
    // and central-bank sources at audit time):
    //   + BTN (Ngultrum, Bhutan), FKP (Falkland Islands Pound), GIP (Gibraltar Pound), KPW (North
    //     Korean Won), MRU (Ouguiya, Mauritania — successor to withdrawn MRO), SHP (Saint Helena
    //     Pound), STN (Dobra, São Tomé and Príncipe — successor to withdrawn STD), SYP (Syrian
    //     Pound), XCG (Caribbean Guilder, Curaçao/Sint Maarten — ISO 4217 Amendment 176, effective
    //     2025-03-31; a real circulating supranational currency, same rationale as XAF/XOF/XCD/XPF).
    //   - HRK (Croatian Kuna — Croatia adopted EUR 2023-01-01), BGN (Bulgarian Lev — Bulgaria
    //     adopted EUR 2026-01-01, BGN ceased sole-legal-tender status 2026-02-01), ANG (Netherlands
    //     Antillean Guilder — replaced by XCG above, ANG ceased legal tender 2025-07-01).
    private static readonly (string Code, string Name)[] Active =
    [
        ("AED", "UAE Dirham"), ("AFN", "Afghani"), ("ALL", "Lek"), ("AMD", "Armenian Dram"),
        ("AOA", "Kwanza"), ("ARS", "Argentine Peso"),
        ("AUD", "Australian Dollar"), ("AWG", "Aruban Florin"), ("AZN", "Azerbaijan Manat"),
        ("BAM", "Convertible Mark"), ("BBD", "Barbados Dollar"), ("BDT", "Taka"),
        ("BHD", "Bahraini Dinar"), ("BIF", "Burundi Franc"),
        ("BMD", "Bermudian Dollar"), ("BND", "Brunei Dollar"), ("BOB", "Boliviano"),
        ("BRL", "Brazilian Real"), ("BSD", "Bahamian Dollar"), ("BTN", "Ngultrum"), ("BWP", "Pula"),
        ("BYN", "Belarusian Ruble"), ("BZD", "Belize Dollar"), ("CAD", "Canadian Dollar"),
        ("CDF", "Congolese Franc"), ("CHF", "Swiss Franc"), ("CLP", "Chilean Peso"),
        ("CNY", "Yuan Renminbi"), ("COP", "Colombian Peso"), ("CRC", "Costa Rican Colon"),
        ("CUP", "Cuban Peso"), ("CVE", "Cabo Verde Escudo"), ("CZK", "Czech Koruna"),
        ("DJF", "Djibouti Franc"), ("DKK", "Danish Krone"), ("DOP", "Dominican Peso"),
        ("DZD", "Algerian Dinar"), ("EGP", "Egyptian Pound"), ("ERN", "Nakfa"),
        ("ETB", "Ethiopian Birr"), ("EUR", "Euro"), ("FJD", "Fiji Dollar"), ("FKP", "Falkland Islands Pound"),
        ("GBP", "Pound Sterling"), ("GEL", "Lari"), ("GHS", "Ghana Cedi"), ("GIP", "Gibraltar Pound"),
        ("GMD", "Dalasi"), ("GNF", "Guinean Franc"), ("GTQ", "Quetzal"),
        ("GYD", "Guyana Dollar"), ("HKD", "Hong Kong Dollar"), ("HNL", "Lempira"),
        ("HTG", "Gourde"), ("HUF", "Forint"), ("IDR", "Rupiah"),
        ("ILS", "New Israeli Sheqel"), ("INR", "Indian Rupee"), ("IQD", "Iraqi Dinar"),
        ("IRR", "Iranian Rial"), ("ISK", "Iceland Krona"), ("JMD", "Jamaican Dollar"),
        ("JOD", "Jordanian Dinar"), ("JPY", "Yen"), ("KES", "Kenyan Shilling"),
        ("KGS", "Som"), ("KHR", "Riel"), ("KMF", "Comorian Franc"), ("KPW", "North Korean Won"), ("KRW", "Won"),
        ("KWD", "Kuwaiti Dinar"), ("KYD", "Cayman Islands Dollar"), ("KZT", "Tenge"),
        ("LAK", "Lao Kip"), ("LBP", "Lebanese Pound"), ("LKR", "Sri Lanka Rupee"),
        ("LRD", "Liberian Dollar"), ("LSL", "Loti"), ("LYD", "Libyan Dinar"),
        ("MAD", "Moroccan Dirham"), ("MDL", "Moldovan Leu"), ("MGA", "Malagasy Ariary"),
        ("MKD", "Denar"), ("MMK", "Kyat"), ("MNT", "Tugrik"), ("MOP", "Pataca"), ("MRU", "Ouguiya"),
        ("MUR", "Mauritius Rupee"), ("MVR", "Rufiyaa"), ("MWK", "Malawi Kwacha"),
        ("MXN", "Mexican Peso"), ("MYR", "Malaysian Ringgit"), ("MZN", "Mozambique Metical"),
        ("NAD", "Namibia Dollar"), ("NGN", "Naira"), ("NIO", "Cordoba Oro"),
        ("NOK", "Norwegian Krone"), ("NPR", "Nepalese Rupee"), ("NZD", "New Zealand Dollar"),
        ("OMR", "Rial Omani"), ("PAB", "Balboa"), ("PEN", "Sol"),
        ("PGK", "Kina"), ("PHP", "Philippine Peso"), ("PKR", "Pakistan Rupee"),
        ("PLN", "Zloty"), ("PYG", "Guarani"), ("QAR", "Qatari Rial"),
        ("RON", "Romanian Leu"), ("RSD", "Serbian Dinar"), ("RUB", "Russian Ruble"),
        ("RWF", "Rwanda Franc"), ("SAR", "Saudi Riyal"), ("SBD", "Solomon Islands Dollar"),
        ("SCR", "Seychelles Rupee"), ("SDG", "Sudanese Pound"), ("SEK", "Swedish Krona"),
        ("SGD", "Singapore Dollar"), ("SHP", "Saint Helena Pound"), ("SLE", "Leone"), ("SOS", "Somali Shilling"),
        ("SRD", "Surinam Dollar"), ("SSP", "South Sudanese Pound"), ("STN", "Dobra"), ("SVC", "El Salvador Colon"),
        ("SYP", "Syrian Pound"), ("SZL", "Lilangeni"), ("THB", "Baht"), ("TJS", "Somoni"), ("TMT", "Turkmenistan New Manat"),
        ("TND", "Tunisian Dinar"), ("TOP", "Pa'anga"), ("TRY", "Turkish Lira"),
        ("TTD", "Trinidad and Tobago Dollar"), ("TWD", "New Taiwan Dollar"), ("TZS", "Tanzanian Shilling"),
        ("UAH", "Hryvnia"), ("UGX", "Uganda Shilling"), ("USD", "US Dollar"),
        ("UYU", "Peso Uruguayo"), ("UZS", "Uzbekistan Sum"), ("VES", "Bolivar Soberano"),
        ("VND", "Dong"), ("VUV", "Vatu"), ("WST", "Tala"), ("XAF", "CFA Franc BEAC"),
        ("XCD", "East Caribbean Dollar"), ("XCG", "Caribbean Guilder"), ("XOF", "CFA Franc BCEAO"), ("XPF", "CFP Franc"),
        ("YER", "Yemeni Rial"), ("ZAR", "Rand"), ("ZMW", "Zambian Kwacha"), ("ZWG", "Zimbabwe Gold"),
    ];

    // Defensive sort: rows above are maintained alphabetically by convention, but this table has
    // already needed mid-list insertions/removals once (the fix-round-1 audit); OrderBy guarantees
    // All stays code-ordered even if a future edit appends out of place.
    public static IReadOnlyList<CurrencyInfo> All { get; } =
        Active.Select(c => new CurrencyInfo(c.Code, Symbols.GetValueOrDefault(c.Code, c.Code), c.Name))
              .OrderBy(c => c.Code, StringComparer.Ordinal)
              .ToList();

    private static readonly IReadOnlyDictionary<string, CurrencyInfo> ByCode =
        All.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>True iff <paramref name="code"/> is an active ISO 4217 currency (case-insensitive, trimmed).</summary>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ByCode.ContainsKey(code.Trim());

    /// <summary>The currency for <paramref name="code"/>, or null if not an active currency.</summary>
    public static CurrencyInfo? Find(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code.Trim(), out CurrencyInfo? c) ? c : null;
}
