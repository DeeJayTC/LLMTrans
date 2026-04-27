using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.Pipeline;

/// Wire and storage format for a PII detector. Separated from the runtime
/// `PiiDetector` (which carries a compiled `Regex`) so packs and custom rules
/// can be persisted, edited in the admin UI, and tested without compiling
/// patterns until they are actually applied.
public sealed record PiiDetectorJson(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("replacement")] string Replacement,
    [property: JsonPropertyName("flags")] IReadOnlyList<string>? Flags = null,
    [property: JsonPropertyName("luhnValidate")] bool LuhnValidate = false);

public static class PiiDetectorSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static IReadOnlyList<PiiDetectorJson> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PiiDetectorJson>();
        try
        {
            return JsonSerializer.Deserialize<List<PiiDetectorJson>>(json, JsonOpts)
                   ?? new List<PiiDetectorJson>();
        }
        catch (JsonException)
        {
            return Array.Empty<PiiDetectorJson>();
        }
    }

    public static string Serialize(IEnumerable<PiiDetectorJson> detectors) =>
        JsonSerializer.Serialize(detectors, JsonOpts);

    /// Compile a stored detector into a runnable `PiiDetector`. Bad regexes throw
    /// `RegexParseException`; callers should catch and skip the offending entry
    /// rather than failing the whole route.
    public static PiiDetector ToDetector(PiiDetectorJson json)
    {
        var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (json.Flags is not null)
        {
            foreach (var f in json.Flags)
            {
                switch (f)
                {
                    case "caseInsensitive": opts |= RegexOptions.IgnoreCase; break;
                    case "multiline": opts |= RegexOptions.Multiline; break;
                }
            }
        }
        return new PiiDetector(json.Kind, json.Replacement, new Regex(json.Pattern, opts), json.LuhnValidate);
    }

    /// Convert a runtime detector back to wire format. Used when seeding the
    /// `pii_packs` table from the in-code `BuiltinDetectors` definitions.
    public static PiiDetectorJson FromDetector(PiiDetector d)
    {
        var flags = new List<string>();
        if ((d.Regex.Options & RegexOptions.IgnoreCase) != 0) flags.Add("caseInsensitive");
        if ((d.Regex.Options & RegexOptions.Multiline) != 0) flags.Add("multiline");
        return new PiiDetectorJson(d.Kind, d.Regex.ToString(), d.Replacement,
            flags.Count > 0 ? flags : null, d.LuhnValidate);
    }
}
