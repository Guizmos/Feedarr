namespace Feedarr.Api.Services.Security;

/// <summary>
/// Distinguishes whether a decryption failure is a permanent credential problem
/// or a transient infrastructure problem.
/// </summary>
public enum DecryptionFailureReason
{
    /// <summary>
    /// The credential stored in the database is corrupt, was encrypted with a different key ring,
    /// or has been tampered with. The user must reconfigure it.
    /// Maps to HTTP 422 Unprocessable Entity.
    /// </summary>
    InvalidStoredSecret,

    /// <summary>
    /// The DataProtection cryptographic subsystem is unavailable: key files are unreadable,
    /// the keys directory is missing, or the key ring could not be loaded at all.
    /// This may be transient (volume not mounted yet, permissions race).
    /// Maps to HTTP 503 Service Unavailable.
    /// </summary>
    CryptoSubsystemUnavailable
}

/// <summary>
/// Thrown when a value marked as encrypted (ENC: prefix) cannot be decrypted with the current
/// key ring. This is a non-recoverable runtime error for the affected credential.
///
/// Possible causes:
///   - The DataProtection key ring was reset (Docker volume wipe, new machine, OS reinstall).
///   - A backup was restored from a machine with a different key ring.
///   - The data/keys directory was deleted, is unreadable, or was corrupted.
///
/// The global exception handler in Program.cs maps <see cref="Reason"/> → HTTP status code:
///   - <see cref="DecryptionFailureReason.InvalidStoredSecret"/>      → 422 Unprocessable Entity
///   - <see cref="DecryptionFailureReason.CryptoSubsystemUnavailable"/> → 503 Service Unavailable
/// </summary>
public sealed class ApiKeyDecryptionException : Exception
{
    /// <summary>
    /// Identifies whether the failure requires user action (422) or is potentially
    /// an infra/transient issue (503).
    /// </summary>
    public DecryptionFailureReason Reason { get; }

    public ApiKeyDecryptionException(
        string message,
        DecryptionFailureReason reason = DecryptionFailureReason.InvalidStoredSecret)
        : base(message)
    {
        Reason = reason;
    }

    public ApiKeyDecryptionException(
        string message,
        Exception inner,
        DecryptionFailureReason reason = DecryptionFailureReason.InvalidStoredSecret)
        : base(message, inner)
    {
        Reason = reason;
    }
}
