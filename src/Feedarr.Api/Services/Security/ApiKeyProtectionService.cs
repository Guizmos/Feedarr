using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Feedarr.Api.Services.Security;

/// <summary>
/// Interface pour le chiffrement/déchiffrement des clés API.
/// </summary>
public interface IApiKeyProtectionService
{
    /// <summary>
    /// Chiffre une clé API en clair.
    /// </summary>
    string Protect(string plainText);

    /// <summary>
    /// Déchiffre une clé API.
    /// - Valeur sans préfixe ENC: → retournée telle quelle (compatibilité pré-chiffrement).
    /// - Valeur avec préfixe ENC: → déchiffrée; si le déchiffrement échoue, lève
    ///   <see cref="ApiKeyDecryptionException"/> (ne retourne JAMAIS la valeur chiffrée en clair).
    /// </summary>
    /// <exception cref="ApiKeyDecryptionException">
    /// La valeur a le préfixe ENC: mais le déchiffrement a échoué (key ring changé, backup
    /// restauré d'une autre machine, clés DataProtection perdues).
    /// </exception>
    string Unprotect(string protectedText);

    /// <summary>
    /// Tente de déchiffrer une valeur protégée sans lever d'exception.
    /// À utiliser quand l'échec est gérable (ex. : backup restore preview, analyse).
    ///
    /// Garanties :
    ///   - Retourne <c>true</c>  → <paramref name="plainText"/> est la valeur déchiffrée (non null, non ENC:).
    ///   - Retourne <c>false</c> → <paramref name="plainText"/> est <c>null</c> — ne jamais l'utiliser.
    ///
    /// Ne retourne JAMAIS la valeur <c>ENC:…</c> dans <paramref name="plainText"/>.
    /// </summary>
    bool TryUnprotect(string protectedText, out string? plainText);

    /// <summary>
    /// Vérifie si une valeur est déjà chiffrée (préfixe ENC:).
    /// </summary>
    bool IsProtected(string value);
}

/// <summary>
/// Service de protection des clés API utilisant Microsoft.AspNetCore.DataProtection.
/// Les clés sont stockées dans App:DataDir/keys et persistent entre les redémarrages.
/// </summary>
public sealed class ApiKeyProtectionService : IApiKeyProtectionService
{
    private const string Purpose = "Feedarr.ApiKeys.v1";
    private const string ProtectedPrefix = "ENC:";

    private readonly IDataProtector _protector;
    private readonly ILogger<ApiKeyProtectionService> _logger;

    public ApiKeyProtectionService(
        IDataProtectionProvider provider,
        ILogger<ApiKeyProtectionService> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        // Si déjà chiffrée, retourner telle quelle
        if (IsProtected(plainText))
            return plainText;

        try
        {
            var encrypted = _protector.Protect(plainText);
            return $"{ProtectedPrefix}{encrypted}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du chiffrement de la clé API");
            throw;
        }
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return protectedText;

        // Valeur sans préfixe ENC: → donnée en clair (compatibilité pré-migration).
        if (!IsProtected(protectedText))
            return protectedText;

        // Valeur chiffrée → le déchiffrement DOIT réussir. Tout échec est fatal : retourner la
        // valeur chiffrée en clair serait une fuite de sécurité et pire, une clé invalide envoyée
        // à l'API externe.
        try
        {
            var encrypted = protectedText[ProtectedPrefix.Length..];
            return _protector.Unprotect(encrypted);
        }
        catch (Exception ex)
        {
            // Distinguish infra failures (503 — may be transient) from credential corruption
            // (422 — requires user action to reconfigure the stored secret).
            var reason = ClassifyDecryptionException(ex);

            if (reason == DecryptionFailureReason.CryptoSubsystemUnavailable)
            {
                _logger.LogError(ex,
                    "Le sous-système cryptographique est indisponible (key ring inaccessible). " +
                    "Vérifiez que le répertoire data/keys est monté et accessible.");
            }
            else
            {
                _logger.LogError(ex,
                    "Échec du déchiffrement d'une clé API (préfixe ENC: présent). " +
                    "Le key ring DataProtection a probablement changé (nouveau volume Docker, " +
                    "restauration de backup depuis une autre machine, suppression de data/keys). " +
                    "La clé doit être reconfigurée dans Paramètres → Fournisseurs externes.");
            }

            throw new ApiKeyDecryptionException(
                reason == DecryptionFailureReason.CryptoSubsystemUnavailable
                    ? "The cryptographic subsystem is unavailable. " +
                      "Check that the data/keys directory is mounted and accessible."
                    : "An API key could not be decrypted. " +
                      "The DataProtection key ring may have changed. " +
                      "Reconfigure the credential in Settings → External Providers.",
                ex,
                reason);
        }
    }

    public bool TryUnprotect(string protectedText, out string? plainText)
    {
        // Null / vide → succès trivial, pas de déchiffrement nécessaire.
        if (string.IsNullOrEmpty(protectedText))
        {
            plainText = protectedText;
            return true;
        }

        // Valeur sans préfixe ENC: → déjà en clair, retourner telle quelle.
        if (!IsProtected(protectedText))
        {
            plainText = protectedText;
            return true;
        }

        // Valeur ENC: → tentative de déchiffrement, sans lancer d'exception.
        try
        {
            var encrypted = protectedText[ProtectedPrefix.Length..];
            plainText = _protector.Unprotect(encrypted);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TryUnprotect: échec du déchiffrement d'une clé API protégée (non fatal – " +
                "utilisé en contexte d'analyse ou de preview)");
            // On NE retourne PAS la valeur chiffrée ni string.Empty :
            // null signale clairement "pas de valeur utilisable".
            plainText = null;
            return false;
        }
    }

    public bool IsProtected(string value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(ProtectedPrefix);
    }

    /// <summary>
    /// Classifies a decryption exception to distinguish a corrupt credential (422)
    /// from a key-ring infrastructure failure (503).
    ///
    /// Heuristics:
    ///   - IOException / UnauthorizedAccessException → key files unreadable → 503
    ///   - InvalidOperationException "key ring" / "no key"   → key ring not loaded → 503
    ///   - CryptographicException, FormatException, others  → bad ciphertext → 422
    /// </summary>
    private static DecryptionFailureReason ClassifyDecryptionException(Exception ex)
    {
        // Walk inner exceptions to find the root cause.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException)
                return DecryptionFailureReason.CryptoSubsystemUnavailable;

            // ASP.NET Core DataProtection throws InvalidOperationException when no keys exist.
            if (current is InvalidOperationException)
            {
                var msg = current.Message;
                if (msg.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("ring", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("protect", StringComparison.OrdinalIgnoreCase))
                    return DecryptionFailureReason.CryptoSubsystemUnavailable;
            }
        }

        // CryptographicException, FormatException, ArgumentException → corrupt ciphertext → 422.
        return DecryptionFailureReason.InvalidStoredSecret;
    }
}
