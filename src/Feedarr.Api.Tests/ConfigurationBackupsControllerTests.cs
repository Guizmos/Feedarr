using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Feedarr.Api.Controllers;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Services.Backup.Configuration;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ArchiveFixture=Feedarr.Api.Tests.ConfigurationArchiveServiceTests.Fixture;
using Target=Feedarr.Api.Tests.ConfigurationRestoreServiceTests.Target;

namespace Feedarr.Api.Tests;

public sealed class ConfigurationBackupsControllerTests
{
    private const string Phrase="A complete API configuration backup!";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task AdminExportPrepareConfirmExposesSafeStateAndPreventsCancellation()
    {
        using var target=new Target(seed:true);var controller=Admin(target);
        var exported=Assert.IsType<FileContentResult>(await controller.Export(new(){Passphrase=Phrase},default));
        Assert.Equal("application/zip",exported.ContentType);
        var before=target.Library();
        await Upload(target,exported.FileContents,Phrase);
        var result=Assert.IsType<OkObjectResult>(await controller.Prepare(default));
        var preview=Assert.IsType<ConfigurationRestorePreview>(result.Value);
        var json=JsonSerializer.Serialize(preview,Json);
        foreach(var secret in new[]{"source-secret","provider-secret","app-secret",Phrase,"ENC:"})Assert.DoesNotContain(secret,json);
        Assert.Equal("no-store",target.Caller.Response.Headers.CacheControl);
        Assert.Equal(400,Status(await controller.Confirm(preview.OperationId,new(),default)));
        Assert.IsType<OkObjectResult>(await controller.Confirm(preview.OperationId,new(){Confirm=true,SecurityChange=true},default));
        var state=Assert.IsType<OkObjectResult>(await controller.State());
        Assert.True(Assert.IsType<ConfigurationRestoreState>(state.Value).Confirmed);
        Assert.Equal(before,target.Library());
        Assert.Equal(409,Status(await controller.Cancel(preview.OperationId,default)));
        Assert.True(target.Store.Read()!.Confirmed);Assert.Equal(before,target.Library());
    }
    [Fact]
    public async Task SetupImportClosesAfterRestartAndPreservesInitialSyncSingleFlight()
    {
        using var origin=new ArchiveFixture();using var target=new Target();
        var api=Initial(target);await Upload(target,await origin.Service.ExportAsync(Phrase),Phrase);
        var p=Assert.IsType<ConfigurationRestorePreview>(Assert.IsType<OkObjectResult>(await api.Prepare(default)).Value);
        Assert.IsType<OkObjectResult>(await api.Confirm(p.OperationId,new(){Confirm=true,SecurityChange=true},default));
        target.RestartService();target.Restore.ApplyAtStartup();api=Initial(target);
        Assert.Equal(403,Status(await api.State()));
        var state=Assert.IsType<ConfigurationRestoreState>(Assert.IsType<OkObjectResult>(await Admin(target).State()).Value);
        Assert.NotNull(state.Applied);Assert.Null(state.Pending);
        var calls=0;await target.Restore.RunInitialSyncCycleAsync((_,_)=>{calls++;return Task.FromResult(true);},default);
        Assert.False(await target.Restore.RunInitialSyncCycleAsync((_,_)=>{calls++;return Task.FromResult(true);},default));Assert.Equal(1,calls);
    }
    [Fact]
    public async Task IncompleteSetupResumesOnlyMissingStepsAndCompletesThroughValidatedRoute()
    {
        using var origin=new ArchiveFixture();using var target=new Target();
        using(var c=origin.Db.Open())origin.Set(c,"security",new SecuritySettings());
        await Upload(target,await origin.Service.ExportAsync(Phrase),Phrase);var api=Initial(target);
        var p=Assert.IsType<ConfigurationRestorePreview>(Assert.IsType<OkObjectResult>(await api.Prepare(default)).Value);
        await api.Confirm(p.OperationId,new(){Confirm=true,SecurityChange=true},default);target.Restore.ApplyAtStartup();
        var state=Assert.IsType<ConfigurationRestoreState>(Assert.IsType<OkObjectResult>(await api.State()).Value);
        Assert.Equal(new[]{3,8},state.RemainingSetupSteps);
        Assert.Equal(409,Status(await api.Complete(default)));
        target.Settings.SaveSecurity(new SecuritySettings{Authentication="basic",Username="configured",PasswordHash=Convert.ToBase64String(new byte[32]),PasswordSalt=Convert.ToBase64String(new byte[16])});
        Assert.IsType<OkObjectResult>(await api.Complete(default));Assert.True(target.Setup.IsSetupCompleted());
        using var db=target.Data.Db.Open();Assert.Equal("pending",db.ExecuteScalar<string>("SELECT initial_sync_state FROM configuration_restore_operations"));
        Assert.Equal(403,Status(await api.Complete(default)));
    }
    [Fact]
    public async Task InvalidPassphraseArchiveAndOversizedUploadAreSafeFailures()
    {
        using var origin=new ArchiveFixture();using var target=new Target();var api=Initial(target);
        await Upload(target,await origin.Service.ExportAsync(Phrase),"A definitely incorrect passphrase!");
        var wrong=await api.Prepare(default);Assert.Equal(422,Status(wrong));
        Assert.DoesNotContain(Phrase,JsonSerializer.Serialize(wrong,Json));
        await Upload(target,[1,2,3],Phrase);Assert.Equal(422,Status(await api.Prepare(default)));Assert.Null(target.Store.Read());
        target.Caller.Request.ContentLength=ConfigurationBackupControllerBase.UploadLimit+1;
        target.Caller.Request.Body=new ThrowingReadStream();Assert.Equal(413,Status(await api.Prepare(default)));
    }
    [Fact]
    public async Task SetupAndAdminRoutesRequireAuthAndCsrfIndependentlyOfArchivePhrase()
    {
        using var target=new Target();var api=Initial(target);
        target.Caller.Items.Remove(BasicAuthMiddleware.AuthPassedKey);Assert.Equal(403,Status(await api.State()));
        target.Caller.Items[BasicAuthMiddleware.AuthPassedKey]=true;
        target.Caller.Request.Headers.Clear();Assert.Equal(403,Status(await api.State()));
        target.Caller.Request.Headers[RequestForgeryProtection.RequestHeaderName]="1";
        Assert.Equal(403,Status(await Admin(target).State()));
        target.Caller.Connection.RemoteIpAddress=IPAddress.Parse("8.8.8.8");Assert.Equal(403,Status(await api.State()));
    }
    [Theory]
    [InlineData("/api/setup/configuration-restore/prepare","POST",true)]
    [InlineData("/api/setup/configuration-restore/state","GET",true)]
    [InlineData("/api/setup/configuration-restore/complete","POST",true)]
    [InlineData("/api/setup/configuration-restore/export","POST",false)]
    [InlineData("/api/configuration-backups/export","POST",false)]
    [InlineData("/api/system/backups","POST",false)]
    public async Task BootstrapTokenScopeIsNarrowAndDoesNotOpenExistingBackupRoutes(string path,string method,bool allowed)
    {
        using var target=new Target();var context=new DefaultHttpContext();context.Connection.RemoteIpAddress=IPAddress.Parse("8.8.8.8");
        context.Request.Method=method;context.Request.Path=path;context.Response.Body=new MemoryStream();
        context.Request.Headers[SmartAuthPolicy.BootstrapTokenHeader]=target.Tokens.IssueToken();
        var passed=false;var middleware=new BasicAuthMiddleware(_=>{passed=true;return Task.CompletedTask;},new ConfigurationBuilder().Build());
        await middleware.InvokeAsync(context,target.Settings,target.Cache,target.Setup,target.Tokens,
            new AuthThrottleService(new Feedarr.Api.Options.BasicAuthThrottleOptions(),TimeProvider.System),NullLogger<BasicAuthMiddleware>.Instance);
        Assert.Equal(allowed,passed);
        if(allowed)Assert.True(context.Items[BasicAuthMiddleware.BootstrapTokenActiveKey] is true);
    }
    [Fact]
    public async Task ExistingLibraryRetentionAndLegacyRestoreStatesRemainVisible()
    {
        using var origin=new ArchiveFixture();using var target=new Target(seed:true);
        target.AddHistoricalLibrary();using(var c=target.Data.Db.Open())target.Data.Set(c,"general",new GeneralSettings{RssLimitPerCategory=100,RssLimitGlobalPerSource=500});
        var before=target.Library();var bytes=target.PosterBytes();await Upload(target,await origin.Service.ExportAsync(Phrase),Phrase);
        var api=Admin(target);var p=Assert.IsType<ConfigurationRestorePreview>(Assert.IsType<OkObjectResult>(await api.Prepare(default)).Value);
        Assert.True(p.StricterRetention);Assert.Equal(1,p.DisabledHistoricalSources);
        await api.Confirm(p.OperationId,new(){Confirm=true,SecurityChange=true},default);target.Restore.ApplyAtStartup();
        var state=Assert.IsType<ConfigurationRestoreState>(Assert.IsType<OkObjectResult>(await api.State()).Value);
        Assert.True(state.Applied!.RetentionPending);Assert.Equal("held",state.Applied.SyncState);
        Assert.Equal(400,Status(await api.Retention(p.OperationId,new(),default)));
        Assert.Equal(409,Status(await api.Sync(p.OperationId,new(){Confirm=true},default)));
        await api.Retention(p.OperationId,new(){Confirm=true},default);await api.Sync(p.OperationId,new(){Confirm=true},default);
        Assert.Equal(before,target.Library());Assert.Equal(bytes,target.PosterBytes());
        using var other=new Target();other.PrepareLegacy();
        var old=Assert.IsType<ConfigurationRestoreState>(Assert.IsType<OkObjectResult>(await Initial(other).State()).Value);Assert.True(old.LegacyRestorePending);
        await Upload(other,await origin.Service.ExportAsync(Phrase),Phrase);Assert.Equal(409,Status(await Initial(other).Prepare(default)));
    }
    [Fact]
    public async Task LegacyArchiveImportUsesConfigurationOnlyAndReportsMissingCredentials()
    {
        using var origin=new ArchiveFixture();using var target=new Target(seed:true);
        target.AddHistoricalLibrary();var before=target.Library();var posters=target.PosterBytes();
        await Upload(target,origin.LegacyArchive(),"");
        var preview=Assert.IsType<ConfigurationRestorePreview>(Assert.IsType<OkObjectResult>(await Admin(target).Prepare(default)).Value);
        Assert.True(preview.Configuration.MissingSecrets>0);Assert.False(preview.CanCompleteSetup);
        Assert.Equal(before,target.Library());Assert.Equal(posters,target.PosterBytes());
    }
    [Theory]
    [InlineData("https://attacker.invalid",false)]
    [InlineData("http://localhost",true)]
    public async Task InitialImportIsNotExemptFromOriginProtection(string origin,bool allowed)
    {
        using var target=new Target();var context=target.Caller;
        context.Request.Method="POST";context.Request.Path="/api/setup/configuration-restore/prepare";
        context.Request.Scheme="http";context.Request.Host=new HostString("localhost");
        context.Request.Headers["Origin"]=origin;context.Response.Body=new MemoryStream();
        var passed=false;var guard=new AntiCsrfOriginMiddleware(_=>{passed=true;return Task.CompletedTask;});
        await guard.InvokeAsync(context,new ConfigurationBuilder().Build(),NullLogger<AntiCsrfOriginMiddleware>.Instance);
        Assert.Equal(allowed,passed);
    }
    [Fact]
    public void HeavyActionsHaveDedicatedRateAndSizeLimits()
    {
        foreach(var type in new[]{typeof(ConfigurationBackupsController),typeof(SetupConfigurationRestoreController)})
        {
            var prepare=type.GetMethod("Prepare")!;
            Assert.Equal("configuration-backup",prepare.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
            Assert.Equal(ConfigurationBackupControllerBase.UploadLimit,((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)prepare.GetCustomAttribute<RequestSizeLimitAttribute>()!).MaxRequestBodySize);
        }
    }
    private static int Status(IActionResult result)=>Assert.IsAssignableFrom<ObjectResult>(result).StatusCode??200;
    private static ConfigurationBackupsController Admin(Target t)=>new(t.Data.Service,t.Restore,t.Setup){ControllerContext=new(){HttpContext=t.Caller}};
    private static SetupConfigurationRestoreController Initial(Target t)=>new(t.Data.Service,t.Restore,t.Setup){ControllerContext=new(){HttpContext=t.Caller}};
    private static async Task Upload(Target target,byte[] bytes,string phrase)
    {
        using var form=new MultipartFormDataContent();form.Add(new StringContent(phrase),"passphrase");form.Add(new ByteArrayContent(bytes),"archive","configuration.zip");
        target.Caller.Request.ContentType=form.Headers.ContentType!.ToString();target.Caller.Request.ContentLength=null;
        target.Caller.Request.Body=new MemoryStream(await form.ReadAsByteArrayAsync());
    }
    private sealed class ThrowingReadStream:MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>throw new InvalidOperationException("Oversized body must not be read");
    }
}
