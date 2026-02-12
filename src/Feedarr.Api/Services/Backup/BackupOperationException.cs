using Microsoft.AspNetCore.Http;

namespace Feedarr.Api.Services.Backup;

public sealed class BackupOperationException : Exception
{
    public int StatusCode { get; }

    public BackupOperationException(string message, int statusCode = StatusCodes.Status500InternalServerError)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public BackupOperationException(string message, Exception innerException, int statusCode = StatusCodes.Status500InternalServerError)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
