using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Feedarr.Api.Services.Security;

/// <summary>
/// Encrypteur XML personnalisé utilisant AES-256-GCM pour chiffrer les clés DataProtection.
/// Fonctionne sur toutes les plateformes (Windows, Linux, Docker).
/// </summary>
public sealed class AesXmlEncryptor : IXmlEncryptor
{
    private readonly byte[] _key;

    public AesXmlEncryptor(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes (256 bits)", nameof(key));
        _key = key;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString());

        // Génère un nonce unique pour chaque chiffrement
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Format: nonce (12) + tag (16) + ciphertext
        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        var encryptedElement = new XElement("encryptedKey",
            new XComment("Encrypted with AES-256-GCM"),
            new XElement("value", Convert.ToBase64String(combined))
        );

        return new EncryptedXmlInfo(encryptedElement, typeof(AesXmlDecryptor));
    }
}

/// <summary>
/// Décrypteur XML correspondant à AesXmlEncryptor.
/// </summary>
public sealed class AesXmlDecryptor : IXmlDecryptor
{
    private readonly IServiceProvider _services;

    public AesXmlDecryptor(IServiceProvider services)
    {
        _services = services;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var keyProvider = _services.GetRequiredService<IMasterKeyProvider>();
        var key = keyProvider.GetMasterKey();

        var combined = Convert.FromBase64String(encryptedElement.Element("value")!.Value);

        // Extract nonce (12) + tag (16) + ciphertext
        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[combined.Length - 28];

        Buffer.BlockCopy(combined, 0, nonce, 0, 12);
        Buffer.BlockCopy(combined, 12, tag, 0, 16);
        Buffer.BlockCopy(combined, 28, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        var xml = System.Text.Encoding.UTF8.GetString(plaintext);
        return XElement.Parse(xml);
    }
}

/// <summary>
/// Interface pour fournir la clé maître.
/// </summary>
public interface IMasterKeyProvider
{
    byte[] GetMasterKey();
}

/// <summary>
/// Fournisseur de clé maître qui lit depuis un fichier ou une variable d'environnement.
/// </summary>
public sealed class FileMasterKeyProvider : IMasterKeyProvider
{
    private readonly byte[] _key;

    public FileMasterKeyProvider(string keysPath)
    {
        var keyFilePath = Path.Combine(keysPath, ".master-key");

        // Priorité 1: Variable d'environnement (recommandé pour Docker secrets)
        var envKey = Environment.GetEnvironmentVariable("FEEDARR_MASTER_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            // Dérive une clé 256-bit à partir de la variable d'env
            _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(envKey));
            return;
        }

        // Priorité 2: Fichier de clé existant
        if (File.Exists(keyFilePath))
        {
            _key = File.ReadAllBytes(keyFilePath);
            if (_key.Length != 32)
                throw new InvalidOperationException("Master key file is corrupted (expected 32 bytes)");
            return;
        }

        // Priorité 3: Génère une nouvelle clé et la sauvegarde
        _key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyFilePath, _key);

        // Permissions restrictives sur Linux (600)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public byte[] GetMasterKey() => _key;
}
