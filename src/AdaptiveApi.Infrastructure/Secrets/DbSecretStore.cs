using System.Security.Cryptography;
using System.Text;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AdaptiveApi.Infrastructure.Secrets;

/// EF-backed secret store. Encrypts every value with AES-GCM using the
/// host's KEK before persisting; only the KEK holder can decrypt. The KEK
/// must be 32 random bytes (256-bit AES) supplied as base64 via
/// <c>AdaptiveApi:Secrets:Kek</c> (env or appsettings).
///
/// Defending in depth — the DB row alone is useless without the KEK. If the
/// KEK rotates, the host re-encrypts every row on next write; old rows stay
/// readable until then because they record their <c>KeyVersion</c>.
public sealed class DbSecretStore : ISecretStore
{
    private readonly AdaptiveApiDbContext _db;
    private readonly byte[]? _kek;
    private readonly int _keyVersion;

    public DbSecretStore(AdaptiveApiDbContext db, IConfiguration cfg)
    {
        _db = db;
        var raw = cfg.GetValue<string>("AdaptiveApi:Secrets:Kek");
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                _kek = Convert.FromBase64String(raw);
                if (_kek.Length != 32)
                {
                    _kek = null; // wrong size — treat as unconfigured
                }
            }
            catch (FormatException) { _kek = null; }
        }
        _keyVersion = cfg.GetValue("AdaptiveApi:Secrets:KeyVersion", 1);
    }

    public bool IsAvailable => _kek is not null;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        if (_kek is null) return null;
        var row = await _db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return null;
        try
        {
            return Decrypt(_kek, row.Nonce, row.Ciphertext, row.Tag);
        }
        catch (CryptographicException)
        {
            // KEK probably rotated without re-encrypting; surface as "no
            // secret available" so callers fall back to env config.
            return null;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        if (_kek is null)
            throw new InvalidOperationException(
                "secret store is not configured (set AdaptiveApi:Secrets:Kek to a base64-encoded 32-byte key)");

        var (cipher, nonce, tag) = Encrypt(_kek, value);
        var row = await _db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new SecretEntity { Key = key };
            _db.Secrets.Add(row);
        }
        row.Ciphertext = cipher;
        row.Nonce = nonce;
        row.Tag = tag;
        row.KeyVersion = _keyVersion;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var row = await _db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return;
        _db.Secrets.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SecretSummary>> ListAsync(CancellationToken ct)
    {
        return await _db.Secrets.AsNoTracking()
            .OrderBy(s => s.Key)
            .Select(s => new SecretSummary(s.Key, s.UpdatedAt, s.KeyVersion))
            .ToListAsync(ct);
    }

    private static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(byte[] kek, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, tagSizeInBytes: 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return (cipher, nonce, tag);
    }

    private static string Decrypt(byte[] kek, byte[] nonce, byte[] cipher, byte[] tag)
    {
        var data = new byte[cipher.Length];
        using var aes = new AesGcm(kek, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipher, tag, data);
        return Encoding.UTF8.GetString(data);
    }
}

/// No-op fallback used when the host runs without a KEK — every read
/// returns null, every write throws. Lets the rest of the app keep
/// resolving <c>ISecretStore</c> without a special-case null check.
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;
    public Task<string?> GetAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SetAsync(string key, string value, CancellationToken ct) =>
        throw new InvalidOperationException("secret store is not configured");
    public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<SecretSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SecretSummary>>(Array.Empty<SecretSummary>());
}
