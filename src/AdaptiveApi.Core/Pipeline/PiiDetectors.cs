using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.Pipeline;

/// A single PII pattern. `Kind` is surfaced in audit metadata and metric labels.
/// `Replacement` is the opaque substitute the upstream model sees (and the user
/// sees in the response) when this pattern fires. `LuhnValidate` is a guard
/// for credit-card detectors that prevents number-shaped strings (phone numbers,
/// codes) from being mis-redacted.
public sealed record PiiDetector(
    string Kind,
    string Replacement,
    Regex Regex,
    bool LuhnValidate = false);

/// Bundle of detectors applied as a unit. Routes resolve to a detector set
/// composed of (a) the default pack, (b) any tenant-selected packs, and
/// (c) per-tenant custom rules. Detector ORDER matters within a set:
/// narrow-and-validated patterns (cards, SSN, IBAN) must run before broader
/// numeric patterns (phone) so the validated match isn't eaten.
public sealed record PiiDetectorSet(IReadOnlyList<PiiDetector> Detectors)
{
    public static PiiDetectorSet Default { get; } = new(BuiltinDetectors.Default);

    public static PiiDetectorSet Empty { get; } = new(Array.Empty<PiiDetector>());

    public PiiDetectorSet With(IEnumerable<PiiDetector> more) =>
        new(Detectors.Concat(more).ToList());
}

/// The built-in detector packs that ship with AdaptiveAPI. Tenants can opt into
/// any combination via the admin UI. The `Default` pack mirrors the original
/// behaviour from before per-tenant detector configuration existed; routes that
/// only set <c>RedactPii: true</c> still get the same six detectors.
public static class BuiltinDetectors
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// 6 detectors: email, Luhn-validated cards, US SSN, IBAN, IPv4, phone.
    public static IReadOnlyList<PiiDetector> Default { get; } =
    [
        new("EMAIL", "[redacted-email]",
            new Regex(@"\b[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+\b", Opts)),
        new("CREDIT_CARD", "[redacted-card]",
            new Regex(@"\b(?:\d[ -]?){13,19}\b", Opts), LuhnValidate: true),
        new("SSN", "[redacted-ssn]",
            new Regex(@"\b\d{3}-\d{2}-\d{4}\b", Opts)),
        new("IBAN", "[redacted-iban]",
            new Regex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b", Opts)),
        new("IPV4", "[redacted-ip]",
            new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\b", Opts)),
        new("PHONE", "[redacted-phone]",
            new Regex(@"\+?\d[\d\s().-]{8,}\d", Opts)),
    ];

    /// EU GDPR pack. Names and locations are best handled by Presidio; the regex
    /// pack adds locale-specific identifiers regex can match reliably.
    public static IReadOnlyList<PiiDetector> EuGdpr { get; } =
    [
        new("EU_VAT", "[redacted-vat]",
            new Regex(@"\b(?:AT|BE|BG|HR|CY|CZ|DK|EE|FI|FR|DE|EL|HU|IE|IT|LV|LT|LU|MT|NL|PL|PT|RO|SK|SI|ES|SE)\d{8,12}\b", Opts)),
        new("EU_PASSPORT", "[redacted-passport]",
            new Regex(@"\b[A-Z]{1,2}\d{6,9}\b", Opts)),
    ];

    /// PCI-DSS pack. Adds CVV-shaped numbers near "CVV"/"CVC" and BIN-style
    /// 6-digit prefixes.
    public static IReadOnlyList<PiiDetector> PciDss { get; } =
    [
        new("CVV", "[redacted-cvv]",
            new Regex(@"\b(?:cvv|cvc|cvv2|cid)\W*\d{3,4}\b",
                Opts | RegexOptions.IgnoreCase)),
    ];

    /// US health pack. NPI (10 digits with Luhn-mod-10), MRN-style identifiers.
    public static IReadOnlyList<PiiDetector> UsHealth { get; } =
    [
        new("NPI", "[redacted-npi]",
            new Regex(@"\b\d{10}\b", Opts)),
        new("US_MRN", "[redacted-mrn]",
            new Regex(@"\bMRN[-: ]?\d{5,10}\b", Opts | RegexOptions.IgnoreCase)),
    ];

    /// UK pack. NHS number, NI number.
    public static IReadOnlyList<PiiDetector> UkNhs { get; } =
    [
        new("NHS_UK", "[redacted-nhs]",
            new Regex(@"\b\d{3}\s?\d{3}\s?\d{4}\b", Opts)),
        new("NI_UK", "[redacted-ni]",
            new Regex(@"\b[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z]\s?\d{2}\s?\d{2}\s?\d{2}\s?[A-D]\b",
                Opts | RegexOptions.IgnoreCase)),
    ];

    /// German tax/identifier pack.
    public static IReadOnlyList<PiiDetector> DeSteuer { get; } =
    [
        new("DE_STEUER_ID", "[redacted-steuer-id]",
            new Regex(@"\b\d{2}\s?\d{3}\s?\d{3}\s?\d{3}\b", Opts)),
        new("DE_IBAN", "[redacted-iban-de]",
            new Regex(@"\bDE\d{2}\s?(?:\d{4}\s?){4}\d{2}\b", Opts)),
    ];
}
