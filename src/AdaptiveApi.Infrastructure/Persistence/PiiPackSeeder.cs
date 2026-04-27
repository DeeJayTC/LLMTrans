using AdaptiveApi.Core.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Persistence;

/// Seeds the `pii_packs` table from the in-code `BuiltinDetectors`. Idempotent:
/// re-running keeps existing rows (so admins can hand-edit a pack's regex set
/// in the DB without losing changes on next boot). Only inserts packs that do
/// not already exist by slug.
public static class PiiPackSeeder
{
    private static readonly (string Slug, string Name, string Description, IReadOnlyList<PiiDetector> Detectors, int Ordinal)[] Packs =
    {
        ("pack_default", "Default",
            "Email, Luhn-validated cards, US SSN, IBAN, IPv4, phone. The set every route gets when only `RedactPii: true` is configured.",
            BuiltinDetectors.Default, 0),
        ("pack_eu_gdpr", "EU GDPR",
            "EU VAT numbers and passport-shaped identifiers. Pair with Presidio for names and locations.",
            BuiltinDetectors.EuGdpr, 1),
        ("pack_pci_dss", "PCI-DSS",
            "Adds CVV/CVC patterns near common labels.",
            BuiltinDetectors.PciDss, 2),
        ("pack_us_health", "US health",
            "NPI and MRN-style identifiers. Pair with HIPAA-aware redaction for names and dates.",
            BuiltinDetectors.UsHealth, 3),
        ("pack_uk_nhs", "UK NHS",
            "NHS numbers and UK National Insurance numbers.",
            BuiltinDetectors.UkNhs, 4),
        ("pack_de_steuer", "Germany",
            "German Steuer-ID and DE-prefixed IBANs.",
            BuiltinDetectors.DeSteuer, 5),
    };

    public static async Task EnsureSeededAsync(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var existing = await db.PiiPacks.AsNoTracking()
            .Select(p => p.Slug).ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);

        var added = false;
        foreach (var (slug, name, description, detectors, ordinal) in Packs)
        {
            if (existingSet.Contains(slug)) continue;
            var json = PiiDetectorSerializer.Serialize(detectors.Select(PiiDetectorSerializer.FromDetector));
            db.PiiPacks.Add(new PiiPackEntity
            {
                Id = slug,
                Slug = slug,
                Name = name,
                Description = description,
                DetectorsJson = json,
                IsBuiltin = true,
                Ordinal = ordinal,
            });
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }
}
