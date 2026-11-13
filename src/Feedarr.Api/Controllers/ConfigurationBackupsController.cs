using System.Security.Cryptography;
using System.Text;
using Feedarr.Api.Services.Backup;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Feedarr.Api.Controllers;

public abstract class ConfigurationBackupControllerBase(
    ConfigurationArchiveService archives, ConfigurationRestoreService restore, SetupStateService setup) : ControllerBase
{
    internal const long UploadLimit=64L*1024*1024+16*1024;
    protected abstract bool InitialSetup {get;}
    protected Task<IActionResult> Execute(Func<Task<IActionResult>> action) => ExecuteCore(action);
    private async Task<IActionResult> ExecuteCore(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl="no-store";
        try
        {
            if(InitialSetup && setup.IsSetupCompleted())throw new BackupOperationException(InitialSetup?"setup_closed":"setup_required",403);
            restore.AuthorizeRequest(HttpContext);
            return await action();
        }
        catch(ConfigurationArchiveException ex)
        {
            return StatusCode(ex.Code==ConfigurationArchiveError.SizeLimitExceeded?413:422,new{code=ex.Code.ToString(),message=ArchiveMessage(ex.Code)});
        }
        catch(BackupOperationException ex){return StatusCode(ex.StatusCode,new{code=ex.Message,message=OperationMessage(ex.Message)});}
        catch(OperationCanceledException) when(HttpContext.RequestAborted.IsCancellationRequested){return StatusCode(499);}
        catch(Exception){return StatusCode(500,new{code="configuration_backup_failed",message="L’opération de configuration a échoué. Aucun détail sensible n’est exposé."});}
    }
    protected Task<IActionResult> StateCore()=>Execute(()=>Task.FromResult<IActionResult>(Ok(restore.GetState(HttpContext))));
    protected Task<IActionResult> PrepareCore(CancellationToken ct)=>Execute(async()=>
    {
        if(Request.ContentLength>UploadLimit)throw new BackupOperationException("upload_too_large",413);
        var type=MediaTypeHeaderValue.TryParse(Request.ContentType,out var parsed)?parsed:null;
        var boundary=type?.Boundary.Value?.Trim('"');
        if(type?.MediaType.Value!="multipart/form-data" || string.IsNullOrEmpty(boundary) || boundary.Length>200)
            throw new BackupOperationException("invalid_upload",400);
        var reader=new MultipartReader(boundary,Request.Body){HeadersCountLimit=8,HeadersLengthLimit=4096};
        byte[]? data=null;string passphrase="";var seen=new HashSet<string>();
        try
        {
            MultipartSection? section;
            while((section=await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if(!ContentDispositionHeaderValue.TryParse(section.ContentDisposition,out var disposition))throw new BackupOperationException("invalid_upload",400);
                var name=disposition.Name.Value?.Trim('"')??"";
                if(!seen.Add(name) || name is not ("archive" or "passphrase"))throw new BackupOperationException("invalid_upload",400);
                var bytes=await ReadMemory(section.Body,name=="archive"?64*1024*1024:4096,ct);
                if(name=="archive")data=bytes;
                else{try{passphrase=new UTF8Encoding(false,true).GetString(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}}
            }
            if(data is null || data.Length==0)throw new BackupOperationException("invalid_upload",400);
            using var input=new MemoryStream(data,false);
            return Ok(await restore.PrepareAsync(input,passphrase,HttpContext,ct));
        }
        catch(InvalidDataException){throw new BackupOperationException("invalid_upload",400);}
        catch(DecoderFallbackException){throw new BackupOperationException("invalid_upload",400); }
        finally{if(data is not null)CryptographicOperations.ZeroMemory(data);}
    });
    protected Task<IActionResult> ConfirmCore(string id,ConfigurationConfirmation request,CancellationToken ct)=>Execute(async()=>
    {
        if(!request.Confirm)throw new BackupOperationException("confirmation_required",400);
        await restore.ConfirmAsync(id,HttpContext,request.SecurityChange,ct);return Ok(restore.GetState(HttpContext));
    });
    protected Task<IActionResult> CancelCore(string id,CancellationToken ct)=>Execute(async()=>{await restore.CancelAsync(id,HttpContext,ct);return NoContent();});
    protected Task<IActionResult> CompleteCore(CancellationToken ct)=>Execute(async()=>{await restore.CompleteRestoredSetupAsync(HttpContext,ct);return Ok(new{onboardingDone=true});});
    protected Task<IActionResult> ExportCore(ConfigurationExport request,CancellationToken ct)=>Execute(async()=>
    {
        if(Request.ContentLength>8192)throw new BackupOperationException("upload_too_large",413);
        var bytes=await archives.ExportAsync(request.Passphrase??"",ct);
        return File(bytes,"application/zip",$"feedarr-configuration-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    });
    protected Task<IActionResult> AppliedActionCore(string id,string action,ConfigurationConfirmation request,CancellationToken ct)=>Execute(async()=>
    {
        if(!request.Confirm)throw new BackupOperationException("confirmation_required",400);
        if(action=="retention")await restore.ConfirmRetentionAsync(id,HttpContext,ct);
        else if(action=="sync")await restore.ConfirmInitialSyncAsync(id,HttpContext,ct);
        else await restore.CancelInitialSyncAsync(id,HttpContext,ct);
        return Ok(restore.GetState(HttpContext));
    });
    private static async Task<byte[]> ReadMemory(Stream stream,int limit,CancellationToken ct)
    {
        using var output=new MemoryStream();var buffer=new byte[8192];
        try
        {
            int read;while((read=await stream.ReadAsync(buffer,ct))>0)
            {
                if(output.Length+read>limit)throw new BackupOperationException("upload_too_large",413);
                output.Write(buffer,0,read);
            }
            return output.ToArray();
        }
        finally{CryptographicOperations.ZeroMemory(buffer);if(output.TryGetBuffer(out var bytes))CryptographicOperations.ZeroMemory(bytes.AsSpan());}
    }
    private static string ArchiveMessage(ConfigurationArchiveError code)=>code switch
    {
        ConfigurationArchiveError.AuthenticationFailed=>"Phrase secrète incorrecte ou archive altérée.",
        ConfigurationArchiveError.InvalidPassphrase=>"La phrase secrète doit contenir entre 12 et 1024 caractères.",
        ConfigurationArchiveError.UnreadableSecrets=>"Des secrets locaux ne peuvent pas être déchiffrés. Aucune sauvegarde n’a été créée.",
        ConfigurationArchiveError.SizeLimitExceeded=>"La sauvegarde dépasse les limites de taille autorisées.",
        ConfigurationArchiveError.UnsupportedVersion=>"Cette version de sauvegarde n’est pas compatible.",
        _=>"La sauvegarde est invalide ou sa configuration est incohérente."
    };
    private static string OperationMessage(string code)=>code switch
    {
        "https_configuration_required"=>"Ouvrez Feedarr en HTTPS avant de confirmer une restauration qui impose HTTPS.",
        "setup_closed"=>"La restauration initiale est fermée. Utilisez les paramètres après authentification.",
        "configuration_restore_ambiguous_identity"=>"Plusieurs identités correspondent. La restauration est bloquée pour protéger la bibliothèque.",
        "configuration_restore_changed_or_invalid_state"=>"La configuration ou l’état a changé. Actualisez puis préparez à nouveau la restauration.",
        "configuration_restore_security_confirmation_required"=>"Confirmez explicitement le changement des paramètres de sécurité.",
        "upload_too_large"=>"Le fichier dépasse la taille maximale de 64 Mio.",
        "invalid_upload"=>"Sélectionnez une archive de sauvegarde valide.",
        "configuration_restore_pending" or "configuration_restore_conflict_or_unreadable_state"=>"Une restauration est déjà préparée ou son état nécessite une vérification.",
        _=>"Opération refusée. Vérifiez votre autorisation, l’état de restauration et les confirmations requises."
    };
}
public sealed class ConfigurationExport {public string? Passphrase{get;set;}}
public sealed class ConfigurationConfirmation {public bool Confirm{get;set;}public bool SecurityChange{get;set;}}

[ApiController]
[Route("api/configuration-backups")]
public sealed class ConfigurationBackupsController(ConfigurationArchiveService a,ConfigurationRestoreService r,SetupStateService s):ConfigurationBackupControllerBase(a,r,s)
{
    protected override bool InitialSetup=>false;
    [HttpPost("export"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Export(ConfigurationExport request,CancellationToken ct)=>ExportCore(request,ct);
    [HttpPost("prepare"),RequestSizeLimit(UploadLimit),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Prepare(CancellationToken ct)=>PrepareCore(ct);
    [HttpGet("state")]
    public Task<IActionResult> State()=>StateCore();
    [HttpPost("{id}/confirm"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Confirm(string id,ConfigurationConfirmation request,CancellationToken ct)=>ConfirmCore(id,request,ct);
    [HttpDelete("{id}"),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Cancel(string id,CancellationToken ct)=>CancelCore(id,ct);
    [HttpPost("{id}/retention"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Retention(string id,ConfigurationConfirmation request,CancellationToken ct)=>AppliedActionCore(id,"retention",request,ct);
    [HttpPost("{id}/sync"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Sync(string id,ConfigurationConfirmation request,CancellationToken ct)=>AppliedActionCore(id,"sync",request,ct);
    [HttpPost("{id}/sync/cancel"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> CancelSync(string id,ConfigurationConfirmation request,CancellationToken ct)=>AppliedActionCore(id,"cancel",request,ct);
}

[ApiController]
[Route("api/setup/configuration-restore")]
public sealed class SetupConfigurationRestoreController(ConfigurationArchiveService a,ConfigurationRestoreService r,SetupStateService s):ConfigurationBackupControllerBase(a,r,s)
{
    protected override bool InitialSetup=>true;
    [HttpPost("prepare"),RequestSizeLimit(UploadLimit),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Prepare(CancellationToken ct)=>PrepareCore(ct);
    [HttpGet("state")]
    public Task<IActionResult> State()=>StateCore();
    [HttpPost("{id}/confirm"),RequestSizeLimit(8192),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Confirm(string id,ConfigurationConfirmation request,CancellationToken ct)=>ConfirmCore(id,request,ct);
    [HttpDelete("{id}"),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Cancel(string id,CancellationToken ct)=>CancelCore(id,ct);
    [HttpPost("complete"),EnableRateLimiting("configuration-backup")]
    public Task<IActionResult> Complete(CancellationToken ct)=>CompleteCore(ct);
}
