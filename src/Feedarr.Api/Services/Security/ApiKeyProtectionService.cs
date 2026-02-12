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
    /// Déchiffre une clé API chiffrée.
    /// Retourne la valeur originale si le déchiffrement échoue (clé non chiffrée).
    /// </summary>
    string Unprotect(string protectedText);

    /// <summary>
    /// Tente de déchiffrer une valeur protégée.
    /// </summary>
    bool TryUnprotect(string protectedText, out string plainText);

    /// <summary>
    /// Vérifie si une valeur est déjà chiffrée.
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
        if (TryUnprotect(protectedText, out var plainText))
            return plainText;

        // Si le déchiffrement échoue, on retourne la valeur originale pour préserver la compatibilité.
        return protectedText;
    }

    public bool TryUnprotect(string protectedText, out string plainText)
    {
        plainText = protectedText;
        if (string.IsNullOrEmpty(protectedText))
            return true;

        // Si pas chiffrée (pas de préfixe), retourner telle quelle
        if (!IsProtected(protectedText))
            return true;

        try
        {
            var encrypted = protectedText[ProtectedPrefix.Length..];
            plainText = _protector.Unprotect(encrypted);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du déchiffrement d'une clé API protégée");
            plainText = protectedText;
            return false;
        }
    }

    public bool IsProtected(string value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(ProtectedPrefix);
    }
}
