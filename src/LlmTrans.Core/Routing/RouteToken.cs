using System.Security.Cryptography;
using System.Text;

namespace LlmTrans.Core.Routing;

public static class RouteToken
{
    public const string Prefix = "rt_";

    public static string Generate(string tenantPrefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var body = Base62.Encode(bytes);
        return $"{Prefix}{tenantPrefix}_{body}";
    }

    public static string HashForStorage(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string PrefixOf(string token)
    {
        var second = token.IndexOf('_', Prefix.Length);
        return second < 0 ? token : token[..second];
    }

    private static class Base62
    {
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        public static string Encode(byte[] input)
        {
            var sb = new StringBuilder();
            var n = System.Numerics.BigInteger.Abs(new System.Numerics.BigInteger(input, isUnsigned: true, isBigEndian: true));
            while (n > 0)
            {
                n = System.Numerics.BigInteger.DivRem(n, 62, out var rem);
                sb.Insert(0, Alphabet[(int)rem]);
            }
            return sb.Length == 0 ? "0" : sb.ToString();
        }
    }
}
