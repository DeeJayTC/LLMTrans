namespace AdaptiveApi.Core.Model;

public readonly record struct LanguageCode(string Value)
{
    public static readonly LanguageCode Auto = new("auto");
    public static readonly LanguageCode English = new("en");
    public override string ToString() => Value;
}
