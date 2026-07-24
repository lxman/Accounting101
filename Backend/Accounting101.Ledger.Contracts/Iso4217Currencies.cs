namespace Accounting101.Ledger.Contracts;

/// <summary>An ISO 4217 currency: the 3-letter code, a display symbol (the code itself where there is
/// no well-known glyph), and the English name.</summary>
public sealed record CurrencyInfo(string Code, string Symbol, string Name);

/// <summary>The active ISO 4217 circulating currencies — real national currencies only; the X-codes
/// (no-currency XXX, testing XTS, precious metals XAU/XAG/XPT/XPD, fund/bond codes) are deliberately
/// excluded. Single source of truth for the base-currency allowlist and the display-symbol catalog.
/// To add a currency, add a row to <see cref="All"/> (and a glyph to Symbols if it has a well-known
/// one); tests assert behavior on a sample, not an exact count, so extension is safe.</summary>
public static class Iso4217Currencies
{
    // Well-known single-glyph symbols. Currencies not listed here fall back to their code as the symbol.
    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["CNY"] = "¥",
        ["INR"] = "₹", ["KRW"] = "₩", ["RUB"] = "₽", ["BRL"] = "R$", ["ZAR"] = "R",
        ["TRY"] = "₺", ["ILS"] = "₪", ["THB"] = "฿", ["PHP"] = "₱", ["VND"] = "₫",
        ["NGN"] = "₦", ["UAH"] = "₴", ["PLN"] = "zł", ["CZK"] = "Kč", ["AUD"] = "$",
        ["CAD"] = "$", ["NZD"] = "$", ["HKD"] = "$", ["SGD"] = "$", ["MXN"] = "$",
    };

    // (Code, English name). Active circulating currencies; extend as needed.
    private static readonly (string Code, string Name)[] Active =
    [
        ("AED", "UAE Dirham"), ("AFN", "Afghani"), ("ALL", "Lek"), ("AMD", "Armenian Dram"),
        ("ANG", "Netherlands Antillean Guilder"), ("AOA", "Kwanza"), ("ARS", "Argentine Peso"),
        ("AUD", "Australian Dollar"), ("AWG", "Aruban Florin"), ("AZN", "Azerbaijan Manat"),
        ("BAM", "Convertible Mark"), ("BBD", "Barbados Dollar"), ("BDT", "Taka"),
        ("BGN", "Bulgarian Lev"), ("BHD", "Bahraini Dinar"), ("BIF", "Burundi Franc"),
        ("BMD", "Bermudian Dollar"), ("BND", "Brunei Dollar"), ("BOB", "Boliviano"),
        ("BRL", "Brazilian Real"), ("BSD", "Bahamian Dollar"), ("BWP", "Pula"),
        ("BYN", "Belarusian Ruble"), ("BZD", "Belize Dollar"), ("CAD", "Canadian Dollar"),
        ("CDF", "Congolese Franc"), ("CHF", "Swiss Franc"), ("CLP", "Chilean Peso"),
        ("CNY", "Yuan Renminbi"), ("COP", "Colombian Peso"), ("CRC", "Costa Rican Colon"),
        ("CUP", "Cuban Peso"), ("CVE", "Cabo Verde Escudo"), ("CZK", "Czech Koruna"),
        ("DJF", "Djibouti Franc"), ("DKK", "Danish Krone"), ("DOP", "Dominican Peso"),
        ("DZD", "Algerian Dinar"), ("EGP", "Egyptian Pound"), ("ERN", "Nakfa"),
        ("ETB", "Ethiopian Birr"), ("EUR", "Euro"), ("FJD", "Fiji Dollar"),
        ("GBP", "Pound Sterling"), ("GEL", "Lari"), ("GHS", "Ghana Cedi"),
        ("GMD", "Dalasi"), ("GNF", "Guinean Franc"), ("GTQ", "Quetzal"),
        ("GYD", "Guyana Dollar"), ("HKD", "Hong Kong Dollar"), ("HNL", "Lempira"),
        ("HRK", "Kuna"), ("HTG", "Gourde"), ("HUF", "Forint"), ("IDR", "Rupiah"),
        ("ILS", "New Israeli Sheqel"), ("INR", "Indian Rupee"), ("IQD", "Iraqi Dinar"),
        ("IRR", "Iranian Rial"), ("ISK", "Iceland Krona"), ("JMD", "Jamaican Dollar"),
        ("JOD", "Jordanian Dinar"), ("JPY", "Yen"), ("KES", "Kenyan Shilling"),
        ("KGS", "Som"), ("KHR", "Riel"), ("KMF", "Comorian Franc"), ("KRW", "Won"),
        ("KWD", "Kuwaiti Dinar"), ("KYD", "Cayman Islands Dollar"), ("KZT", "Tenge"),
        ("LAK", "Lao Kip"), ("LBP", "Lebanese Pound"), ("LKR", "Sri Lanka Rupee"),
        ("LRD", "Liberian Dollar"), ("LSL", "Loti"), ("LYD", "Libyan Dinar"),
        ("MAD", "Moroccan Dirham"), ("MDL", "Moldovan Leu"), ("MGA", "Malagasy Ariary"),
        ("MKD", "Denar"), ("MMK", "Kyat"), ("MNT", "Tugrik"), ("MOP", "Pataca"),
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
        ("SGD", "Singapore Dollar"), ("SLE", "Leone"), ("SOS", "Somali Shilling"),
        ("SRD", "Surinam Dollar"), ("SSP", "South Sudanese Pound"), ("SVC", "El Salvador Colon"),
        ("SZL", "Lilangeni"), ("THB", "Baht"), ("TJS", "Somoni"), ("TMT", "Turkmenistan New Manat"),
        ("TND", "Tunisian Dinar"), ("TOP", "Pa'anga"), ("TRY", "Turkish Lira"),
        ("TTD", "Trinidad and Tobago Dollar"), ("TWD", "New Taiwan Dollar"), ("TZS", "Tanzanian Shilling"),
        ("UAH", "Hryvnia"), ("UGX", "Uganda Shilling"), ("USD", "US Dollar"),
        ("UYU", "Peso Uruguayo"), ("UZS", "Uzbekistan Sum"), ("VES", "Bolivar Soberano"),
        ("VND", "Dong"), ("VUV", "Vatu"), ("WST", "Tala"), ("XAF", "CFA Franc BEAC"),
        ("XCD", "East Caribbean Dollar"), ("XOF", "CFA Franc BCEAO"), ("XPF", "CFP Franc"),
        ("YER", "Yemeni Rial"), ("ZAR", "Rand"), ("ZMW", "Zambian Kwacha"), ("ZWG", "Zimbabwe Gold"),
    ];

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
