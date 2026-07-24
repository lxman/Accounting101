# Client Base Currency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each client (set of books) an immutable, set-once base currency (ISO 4217), so the platform can name the single currency its ledger is kept in — the prerequisite for the e-invoicing inbound module's currency gate.

**Architecture:** `BaseCurrency` is per-client host policy stored on `ClientRegistration` in the control DB (the engine and journal never see it — amounts stay bare `decimal`). It is written exactly once by the create-client handler and has no setter; every existing update path is read-modify-write of the whole registration, so it survives untouched. A static ISO 4217 table in the zero-dep Contracts assembly is the single authority for the validation allowlist and the display-symbol catalog, exposed to the frontend via `GET /currencies/catalog` and surfaced per-client through `CapabilitiesResponse`.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal APIs, MongoDB (via `ControlStore`), xUnit + EphemeralMongo (`ApiFixture`, `SharedMongo`), Angular (signals, Jasmine/Karma specs).

## Global Constraints

- .NET 10; latest NuGets; namespaces follow folder structure; build 0 warnings; TDD; commit per slice.
- Engine stays currency-agnostic — **no** change to journal, `Line`, `PostEntryRequest`, ledger core, or any engine invariant. Base currency lives only in the control DB.
- Immutable after provisioning: **no** `SetBaseCurrency` contract, **no** update endpoint. The create-client handler is the sole writer.
- ISO 4217 **real circulating currencies only** — exclude the X-codes (`XXX`, `XTS`, metals `XAU/XAG/XPT/XPD`, fund/bond codes).
- Display is **symbol only** — no per-locale number formatting. Numbers keep their current formatting; only the currency glyph changes.
- Stage explicit file lists per commit; watch for Rider `var`-rewrite churn (verify the staged set before committing).
- Legacy-safe: a `ClientRegistration` document written before this field existed must deserialize to `"USD"`.

## Scope adjustments discovered during planning (read before starting)

Two spec UI items have **no home in the codebase yet** and are therefore deferred out of this plan (documented, not dropped), and one spec test case was reinterpreted during implementation:

1. **Creation dropdown** — there is no client-provisioning UI (provisioning is API-only via `POST /admin/clients`; admin routes are users/access/approval-policy/fiscal/posting-accounts only). Base currency is set through the create-client API until a provisioning screen exists. The `GET /currencies/catalog` endpoint (Task 4) is built now so that screen can consume it later.
2. **Pervasive symbol rendering + read-only settings display** — hardcoding `'USD'` is not confined to `core/format/display.ts`'s `money()` helper. A grep of the shipped code (`grep -rn "formatMoney(.*'USD'" UI/Angular/src --include="*.ts"`, excluding `*.spec.ts`) finds **9 call sites across 6 files**: `core/format/display.ts` (1), `features/journal/entry-form.ts` (1), `features/statements/balance-sheet.ts` (1), `features/statements/income-statement.ts` (1), `features/statements/statement-section.ts` (1), and `features/trial-balance/trial-balance.ts` (4). Making every amount render the client's symbol is a broad UI re-threading not required by the actual consumer (the e-invoicing module reads `BaseCurrency` from the backend). Task 5 lands the frontend *data + symbol vocabulary* (model field, capability signal, expanded symbol map with tests) so a later UI slice can switch rendering on cheaply — sized correctly by this count, not the single call site the spec's Testing section implied.
3. **Whitespace-only `BaseCurrency` is treated as omission, not rejected** — `AdminEndpoints.CreateClient` decides "was a value supplied at all" with `string.IsNullOrWhiteSpace(request.BaseCurrency)`, so a whitespace-only string (e.g. `"   "`) reads the same as `null`/absent and defaults to `"USD"`. The spec's Testing section originally listed a whitespace string among the codes that must yield 422 alongside `"XXX"`/`"XAU"`/`"ZZZ"`; that bullet has been corrected to match the shipped (and kept) behavior — see the spec's "Amended during planning" section.

The backend + contracts (Tasks 1–4) are the complete, independently testable capability and everything the e-invoicing prerequisite needs.

---

### Task 1: ISO 4217 currency table (Contracts)

**Files:**
- Create: `Backend/Accounting101.Ledger.Contracts/Iso4217Currencies.cs`
- Test: `Backend/Accounting101.Ledger.Contracts.Tests/Iso4217CurrenciesTests.cs`

**Interfaces:**
- Produces: `record CurrencyInfo(string Code, string Symbol, string Name)`;
  `static class Iso4217Currencies` with `IReadOnlyList<CurrencyInfo> All`,
  `bool IsKnown(string? code)` (case-insensitive, trims), `CurrencyInfo? Find(string? code)`.

- [ ] **Step 1: Write the failing test**

```csharp
// Backend/Accounting101.Ledger.Contracts.Tests/Iso4217CurrenciesTests.cs
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Contracts.Tests;

public sealed class Iso4217CurrenciesTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("CAD")]
    public void Known_real_currencies_are_recognized(string code) =>
        Assert.True(Iso4217Currencies.IsKnown(code));

    [Theory]
    [InlineData("usd")]   // case-insensitive
    [InlineData("  eur ")] // trimmed
    public void Recognition_is_case_insensitive_and_trimmed(string code) =>
        Assert.True(Iso4217Currencies.IsKnown(code));

    [Theory]
    [InlineData("XXX")]   // no-currency
    [InlineData("XTS")]   // testing
    [InlineData("XAU")]   // gold
    [InlineData("ZZZ")]   // junk
    [InlineData("US")]    // too short
    [InlineData("")]
    [InlineData(null)]
    public void X_codes_and_junk_are_rejected(string? code) =>
        Assert.False(Iso4217Currencies.IsKnown(code));

    [Fact]
    public void Every_row_has_a_nonempty_symbol_and_name()
    {
        Assert.NotEmpty(Iso4217Currencies.All);
        Assert.All(Iso4217Currencies.All, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Code));
            Assert.False(string.IsNullOrWhiteSpace(c.Symbol));
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
        });
    }

    [Fact]
    public void Find_returns_symbol_for_known_and_null_for_unknown()
    {
        Assert.Equal("€", Iso4217Currencies.Find("EUR")!.Symbol);
        Assert.Null(Iso4217Currencies.Find("XXX"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Contracts.Tests --filter Iso4217CurrenciesTests`
Expected: FAIL to compile — `Iso4217Currencies` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// Backend/Accounting101.Ledger.Contracts/Iso4217Currencies.cs
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
```

> Note: `XAF`/`XOF`/`XCD`/`XPF` are real circulating CFA/Caribbean/Pacific-franc currencies (an `X` prefix here denotes a supranational currency, not a non-currency code); they are correctly included. The excluded X-codes are specifically `XXX`, `XTS`, and the metal/fund codes.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Contracts.Tests --filter Iso4217CurrenciesTests`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/Iso4217Currencies.cs Backend/Accounting101.Ledger.Contracts.Tests/Iso4217CurrenciesTests.cs
git commit -m "feat(contracts): ISO 4217 currency table (allowlist + symbols, X-codes excluded)"
```

---

### Task 2: `BaseCurrency` field on `ClientRegistration`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs`
- Test: `Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs` (add cases)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ClientRegistration.BaseCurrency` (`string`, default `"USD"`).

- [ ] **Step 1: Write the failing test**

Add these two tests to the existing `ClientRegistrationTenancyFieldsTests` class (it already has `FreshDbAsync()`):

```csharp
    [Fact]
    public async Task BaseCurrency_round_trips()
    {
        ControlStore control = new(await FreshDbAsync());
        Guid id = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = id, Name = "Eurofirm", DatabaseName = "client_eu", BaseCurrency = "EUR",
        });

        ClientRegistration reg = (await control.GetClientAsync(id))!;
        Assert.Equal("EUR", reg.BaseCurrency);
    }

    [Fact]
    public async Task Legacy_document_without_base_currency_deserializes_to_USD()
    {
        IMongoDatabase db = await FreshDbAsync();
        Guid id = Guid.NewGuid();
        // Write a raw document with NO baseCurrency field, mimicking a pre-field registration.
        await db.GetCollection<BsonDocument>("clients").InsertOneAsync(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(id, GuidRepresentation.Standard),
            ["Name"] = "Legacy", ["DatabaseName"] = "client_legacy",
        });

        ClientRegistration reg = (await new ControlStore(db).GetClientAsync(id))!;
        Assert.Equal("USD", reg.BaseCurrency);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter ClientRegistrationTenancyFieldsTests`
Expected: FAIL to compile — `ClientRegistration.BaseCurrency` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `ClientRegistration` (after `FiscalYearEndMonth`):

```csharp
    /// <summary>The ISO 4217 code of the single currency this client's books are kept in (e.g. "USD",
    /// "EUR", "GBP"). Set once at provisioning and immutable thereafter — changing it would
    /// re-denominate all history. Host policy stored in the control DB, like ApprovalMode; the engine
    /// never sees it. A legacy document stored before this field existed deserializes to "USD".</summary>
    public string BaseCurrency { get; set; } = "USD";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter ClientRegistrationTenancyFieldsTests`
Expected: PASS. (The `= "USD"` initializer covers the missing-field legacy case; the round-trip proves persistence.)

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs Backend/Accounting101.Ledger.Api.Tests/ClientRegistrationTenancyFieldsTests.cs
git commit -m "feat(control): BaseCurrency on ClientRegistration (legacy -> USD)"
```

---

### Task 3: Provisioning — set-once wiring + validation + immutability

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (`CreateClientRequest`, `ClientRegistrationResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs` (`CreateClient`, `SetFiscalYearEnd`, `ListClients` response construction)
- Test: `Backend/Accounting101.Ledger.Api.Tests/CreateClientBaseCurrencyTests.cs` (new)

**Interfaces:**
- Consumes: `Iso4217Currencies.IsKnown` (Task 1); `ClientRegistration.BaseCurrency` (Task 2).
- Produces: `CreateClientRequest.BaseCurrency` (`string?`); `ClientRegistrationResponse` gains a trailing `string BaseCurrency` positional member. **Note for later tasks: `ClientRegistrationResponse` is now `(Guid Id, string Name, string DatabaseName, ApprovalMode ApprovalMode, int FiscalYearEndMonth, string BaseCurrency)`.**

- [ ] **Step 1: Write the failing test**

```csharp
// Backend/Accounting101.Ledger.Api.Tests/CreateClientBaseCurrencyTests.cs
using System.Net;
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CreateClientBaseCurrencyTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public CreateClientBaseCurrencyTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_with_EUR_persists_and_echoes()
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Eurofirm", BaseCurrency = "eur" }); // lower-case on purpose

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal("EUR", body.BaseCurrency); // normalized to upper-case
    }

    [Fact]
    public async Task Create_without_base_currency_defaults_to_USD()
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Domestic" });

        ClientRegistrationResponse body = (await resp.Content.ReadFromJsonAsync<ClientRegistrationResponse>())!;
        Assert.Equal("USD", body.BaseCurrency);
    }

    [Theory]
    [InlineData("XXX")]
    [InlineData("XAU")]
    [InlineData("ZZZ")]
    public async Task Create_with_ineligible_code_is_422(string code)
    {
        HttpClient admin = _fx.AdminClient();
        HttpResponseMessage resp = await admin.PostAsJsonAsync("/admin/clients",
            new CreateClientRequest { Name = "Bad", BaseCurrency = code });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Updating_another_field_preserves_base_currency()
    {
        // Register a EUR client directly, then run the SAME read-modify-write the fiscal-year setter uses,
        // and prove BaseCurrency survives (there is no setter for it; every update carries it forward).
        ControlStore control = _fx.Control();
        Guid id = Guid.NewGuid();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = id, Name = "Eurofirm", DatabaseName = "client_" + id.ToString("N"), BaseCurrency = "EUR",
        });

        ClientRegistration reg = (await control.GetClientAsync(id))!;
        reg.FiscalYearEndMonth = 6;                 // mutate a different field, read-modify-write
        await control.RegisterClientAsync(reg);

        ClientRegistration after = (await control.GetClientAsync(id))!;
        Assert.Equal("EUR", after.BaseCurrency);    // unchanged
        Assert.Equal(6, after.FiscalYearEndMonth);  // the intended change landed
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CreateClientBaseCurrencyTests`
Expected: FAIL to compile — `CreateClientRequest.BaseCurrency` and `ClientRegistrationResponse.BaseCurrency` do not exist.

- [ ] **Step 3: Write minimal implementation**

In `AdminContracts.cs`, add to `CreateClientRequest` (after `FiscalYearEndMonth`):

```csharp
    /// <summary>ISO 4217 code of the books' base currency. Omitted → "USD". Validated against the
    /// ISO 4217 currency allowlist; unknown/ineligible codes are rejected (422). Immutable after
    /// provisioning — there is no setter.</summary>
    public string? BaseCurrency { get; init; }
```

Replace `ClientRegistrationResponse` with the extended positional record:

```csharp
public sealed record ClientRegistrationResponse(
    Guid Id, string Name, string DatabaseName, ApprovalMode ApprovalMode, int FiscalYearEndMonth,
    string BaseCurrency);
```

In `AdminEndpoints.cs`, update `CreateClient` — add validation after the fiscal-year check and set the field, then include it in the response:

```csharp
    private static async Task<IResult> CreateClient(
        CreateClientRequest request, ControlStore control, CancellationToken cancellationToken)
    {
        if (request.FiscalYearEndMonth is < 1 or > 12)
            return Results.Problem("FiscalYearEndMonth must be between 1 and 12.", statusCode: StatusCodes.Status400BadRequest);

        string baseCurrency = string.IsNullOrWhiteSpace(request.BaseCurrency)
            ? "USD"
            : request.BaseCurrency.Trim().ToUpperInvariant();
        if (!Iso4217Currencies.IsKnown(baseCurrency))
            return Results.Problem($"Unknown or ineligible currency '{request.BaseCurrency}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        Guid id = Guid.NewGuid();
        string database = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? "client_" + id.ToString("N")
            : request.DatabaseName;

        ApprovalMode mode = request.ApprovalMode == ApprovalMode.Unspecified
            ? ApprovalMode.TwoPerson
            : request.ApprovalMode;

        ClientRegistration registration = new()
        {
            Id = id,
            Name = request.Name,
            DatabaseName = database,
            ApprovalMode = mode,
            FiscalYearEndMonth = request.FiscalYearEndMonth,
            BaseCurrency = baseCurrency,
        };
        await control.RegisterClientAsync(registration, cancellationToken);

        return Results.Created(
            $"/admin/clients/{id}",
            new ClientRegistrationResponse(id, registration.Name, registration.DatabaseName,
                ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration), registration.BaseCurrency));
    }
```

Update the two other `ClientRegistrationResponse` construction sites so they compile and echo the stored value:

In `SetFiscalYearEnd` (the `return Results.Ok(new ClientRegistrationResponse(...))`):

```csharp
        return Results.Ok(new ClientRegistrationResponse(registration.Id, registration.Name, registration.DatabaseName,
            ApprovalPolicy.ModeOf(registration), FiscalYear.MonthOf(registration), registration.BaseCurrency));
```

In `ListClients` (the `.Select(...)`):

```csharp
        return Results.Ok(clients
            .Select(c => new ClientRegistrationResponse(c.Id, c.Name, c.DatabaseName,
                ApprovalPolicy.ModeOf(c), FiscalYear.MonthOf(c), c.BaseCurrency))
            .ToList());
```

Ensure `AdminEndpoints.cs` already has `using Accounting101.Ledger.Contracts;` (it does) so `Iso4217Currencies` resolves.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CreateClientBaseCurrencyTests`
Expected: PASS (persist+echo, default USD, 422 on ineligible, immutability-on-update).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/AdminEndpoints.cs Backend/Accounting101.Ledger.Api.Tests/CreateClientBaseCurrencyTests.cs
git commit -m "feat(admin): set-once base currency at provisioning (422 on bad code, immutable after)"
```

---

### Task 4: Expose base currency — `CapabilitiesResponse` + `/currencies/catalog`

**Files:**
- Modify: `Backend/Accounting101.Ledger.Contracts/AdminContracts.cs` (`CapabilitiesResponse`)
- Modify: `Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs` (`GetMyCapabilities`)
- Create: `Backend/Accounting101.Ledger.Api/Endpoints/CurrencyCatalogEndpoints.cs`
- Modify: the endpoint registrar that calls `MapCapabilityCatalogEndpoints()` (wire `MapCurrencyCatalogEndpoints()` beside it — see Step 3 for how to locate it)
- Test: `Backend/Accounting101.Ledger.Api.Tests/CurrencyExposureTests.cs` (new)

**Interfaces:**
- Consumes: `Iso4217Currencies.All` (Task 1); `ClientRegistration.BaseCurrency` (Task 2).
- Produces: `CapabilitiesResponse` gains trailing `string BaseCurrency`; `GET /currencies/catalog` → `IReadOnlyList<CurrencyInfo>`. **Note: `CapabilitiesResponse` is now `(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin, IReadOnlyList<string> EnabledModules, ApprovalMode ApprovalMode, string BaseCurrency)`.**

- [ ] **Step 1: Write the failing test**

```csharp
// Backend/Accounting101.Ledger.Api.Tests/CurrencyExposureTests.cs
using System.Net.Http.Json;
using Accounting101.Ledger.Api.Control;
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Tests;

public sealed class CurrencyExposureTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    public CurrencyExposureTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Capabilities_carry_the_clients_base_currency()
    {
        // Mint the http client first (boots the host), then register a EUR client + membership for that user.
        Guid clientId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        HttpClient http = _fx.ClientFor(userId, "EU User", ("role", "Controller"));

        ControlStore control = _fx.Control();
        await control.RegisterClientAsync(new ClientRegistration
        {
            Id = clientId, Name = "Eurofirm", DatabaseName = "client_" + clientId.ToString("N"),
            BaseCurrency = "EUR",
        });
        await control.AddMembershipAsync(userId, clientId, LedgerRole.Controller);

        CapabilitiesResponse caps = (await http.GetFromJsonAsync<CapabilitiesResponse>(
            $"/clients/{clientId}/me/capabilities"))!;
        Assert.Equal("EUR", caps.BaseCurrency);
    }

    [Fact]
    public async Task Currency_catalog_lists_real_currencies_and_excludes_X_codes()
    {
        HttpClient http = _fx.ClientFor(Guid.NewGuid(), "Anyone");
        List<CurrencyInfo> catalog = (await http.GetFromJsonAsync<List<CurrencyInfo>>("/currencies/catalog"))!;

        Assert.Contains(catalog, c => c is { Code: "EUR", Symbol: "€" });
        Assert.Contains(catalog, c => c.Code == "USD");
        Assert.DoesNotContain(catalog, c => c.Code is "XXX" or "XTS" or "XAU");
        Assert.All(catalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Symbol)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CurrencyExposureTests`
Expected: FAIL — `CapabilitiesResponse.BaseCurrency` missing and `/currencies/catalog` returns 404.

- [ ] **Step 3: Write minimal implementation**

In `AdminContracts.cs`, extend `CapabilitiesResponse` with a trailing member:

```csharp
public sealed record CapabilitiesResponse(
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Roles, bool DeploymentAdmin,
    IReadOnlyList<string> EnabledModules, ApprovalMode ApprovalMode, string BaseCurrency);
```

In `CapabilitiesEndpoints.GetMyCapabilities`, pass the client's base currency (default `"USD"` when the client row is somehow absent, matching the existing `?? []` / `TwoPerson` fallbacks):

```csharp
        return Results.Ok(new CapabilitiesResponse(
            membership.Capabilities,
            membership.GrantedRoles.Select(r => r.ToString()).ToList(),
            deploymentAdmin,
            client?.EnabledModules ?? [],
            client is null ? ApprovalMode.TwoPerson : ApprovalPolicy.ModeOf(client),
            client?.BaseCurrency ?? "USD"));
```

Create the catalog endpoint (mirrors `CapabilityCatalogEndpoints`; readable by any authenticated user because amount-display screens, not just admins, resolve symbols from it):

```csharp
// Backend/Accounting101.Ledger.Api/Endpoints/CurrencyCatalogEndpoints.cs
using Accounting101.Ledger.Contracts;

namespace Accounting101.Ledger.Api.Endpoints;

/// <summary>Static ISO 4217 currency catalog (code, symbol, name) — backend truth for the base-currency
/// picker and for resolving display symbols. Any authenticated user may read it.</summary>
public static class CurrencyCatalogEndpoints
{
    public static void MapCurrencyCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/currencies/catalog", () => Results.Ok(Iso4217Currencies.All)).RequireAuthorization();
    }
}
```

Wire it up: find where `MapCapabilityCatalogEndpoints()` is called (run
`grep -rn "MapCapabilityCatalogEndpoints" Backend/Accounting101.Ledger.Api` — it is in the endpoint-mapping
extension, e.g. `Hosting/LedgerEngineExtensions.cs` or a `MapLedgerEndpoints` aggregator) and add
`app.MapCurrencyCatalogEndpoints();` on the line beneath it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter CurrencyExposureTests`
Expected: PASS (capabilities carry EUR; catalog lists real currencies, excludes X-codes).

- [ ] **Step 5: Commit**

```bash
git add Backend/Accounting101.Ledger.Contracts/AdminContracts.cs Backend/Accounting101.Ledger.Api/Endpoints/CapabilitiesEndpoints.cs Backend/Accounting101.Ledger.Api/Endpoints/CurrencyCatalogEndpoints.cs Backend/Accounting101.Ledger.Api/Hosting/LedgerEngineExtensions.cs Backend/Accounting101.Ledger.Api.Tests/CurrencyExposureTests.cs
git commit -m "feat(api): expose base currency in capabilities + GET /currencies/catalog"
```

> If the `MapCurrencyCatalogEndpoints()` wiring lives in a file other than `Hosting/LedgerEngineExtensions.cs`, adjust the `git add` path to the actual file you edited.

---

### Task 5: Frontend — base-currency data + symbol vocabulary

**Files:**
- Modify: `UI/Angular/src/app/core/capabilities/capabilities.ts` (model + empty default)
- Modify: `UI/Angular/src/app/core/capabilities/capability.service.ts` (expose a `baseCurrency` signal)
- Modify: `UI/Angular/src/app/core/format/money-formatter.ts` (expand `SYMBOLS`)
- Test: `UI/Angular/src/app/core/format/money-formatter.spec.ts` (add symbol cases)

**Interfaces:**
- Consumes: backend `CapabilitiesResponse.BaseCurrency` (Task 4).
- Produces: `CapabilitiesResponse.baseCurrency` (TS); `CapabilityService.baseCurrency: Signal<string>`; expanded `SYMBOLS`.

This lands the data + glyph vocabulary only. Switching every rendered amount to the client's symbol (making the pure `money()` helper currency-aware) is a separate follow-on UI slice — see "Scope adjustments" above.

- [ ] **Step 1: Write the failing test**

Add to `money-formatter.spec.ts` (it already imports `formatMoney` and defines `D`/`P` helpers; `every` forces the symbol to show):

```typescript
  it('renders known non-USD symbols', () => {
    expect(formatMoney(1234, 'EUR', P({ currencySymbol: 'every' }))).toBe('€1,234.00');
    expect(formatMoney(50, 'GBP', P({ currencySymbol: 'every' }))).toBe('£50.00');
    expect(formatMoney(1000, 'JPY', P({ currencySymbol: 'every' }))).toBe('¥1,000.00');
  });

  it('falls back to the code for a currency with no glyph', () => {
    expect(formatMoney(10, 'CHF', P({ currencySymbol: 'every' }))).toBe('CHF 10.00');
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd UI/Angular && npm test -- --include='**/money-formatter.spec.ts' --watch=false`
Expected: FAIL — EUR/GBP/JPY currently fall back to `"EUR 1,234.00"` etc. because `SYMBOLS` only has `USD`.

- [ ] **Step 3: Write minimal implementation**

In `money-formatter.ts`, expand the `SYMBOLS` map (keep the `?? currency + ' '` code fallback that follows it):

```typescript
const SYMBOLS: Record<string, string> = {
  USD: '$', EUR: '€', GBP: '£', JPY: '¥', CNY: '¥', INR: '₹', KRW: '₩', RUB: '₽',
  BRL: 'R$', ZAR: 'R', TRY: '₺', ILS: '₪', THB: '฿', PHP: '₱', VND: '₫', NGN: '₦',
  UAH: '₴', PLN: 'zł', CZK: 'Kč', AUD: '$', CAD: '$', NZD: '$', HKD: '$', SGD: '$', MXN: '$',
};
```

In `capabilities.ts`, add `baseCurrency` to the interface and the empty default:

```typescript
export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
  enabledModules: string[];
  approvalMode: ApprovalMode;
  baseCurrency: string;
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = {
  capabilities: [], roles: [], deploymentAdmin: false, enabledModules: [], approvalMode: 'TwoPerson',
  baseCurrency: 'USD',
};
```

In `capability.service.ts`, expose a `baseCurrency` signal alongside the existing `computed` signals (place it beside `approvalMode`, mirroring its shape):

```typescript
  readonly baseCurrency: Signal<string> = computed(() => this.current().baseCurrency);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd UI/Angular && npm test -- --include='**/money-formatter.spec.ts' --watch=false`
Expected: PASS. Then run the capability service spec to confirm the model change compiles:
Run: `cd UI/Angular && npm test -- --include='**/capability.service.spec.ts' --watch=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add UI/Angular/src/app/core/capabilities/capabilities.ts UI/Angular/src/app/core/capabilities/capability.service.ts UI/Angular/src/app/core/format/money-formatter.ts UI/Angular/src/app/core/format/money-formatter.spec.ts
git commit -m "feat(ui): base currency in capabilities model + non-USD symbol vocabulary"
```

---

## Final verification

- [ ] Run the full backend suite: `dotnet test Backend/Accounting101.Ledger.Contracts.Tests Backend/Accounting101.Ledger.Api.Tests` — all green, 0 warnings.
- [ ] Run the frontend format + capability specs (commands above) — green.
- [ ] Confirm no engine/journal/ledger-core file was touched (`git diff --name-only master... | grep -E 'Ledger.Core|Ledger.Mongo/|PostEntryRequest'` returns nothing).
- [ ] Confirm no `SetBaseCurrency`/base-currency update endpoint was added (immutability by omission).

## Self-review (completed against the spec)

- **Spec coverage:** data model (Task 2), ISO 4217 real-currency table + symbols (Task 1), set-once provisioning + 422 validation (Task 3), immutability — no setter + read-modify-write preservation test (Tasks 2–3), read/display via `CapabilitiesResponse` + `/currencies/catalog` (Task 4), frontend data + symbol vocabulary (Task 5). Deferred-with-documentation: provisioning dropdown + read-only settings display + pervasive symbol rendering (no UI home yet — "Scope adjustments").
- **Placeholder scan:** none — every step carries complete code; the only lookup instruction (locating the `MapCapabilityCatalogEndpoints` call site) gives the exact grep and the likely file.
- **Type consistency:** `ClientRegistrationResponse` and `CapabilitiesResponse` positional extensions are stated in the Interfaces blocks and every construction site is updated in the same task; `Iso4217Currencies.IsKnown/All/Find` names are used identically across Tasks 1/3/4; TS `baseCurrency` matches the backend `BaseCurrency` JSON member (camelCase over the wire).
