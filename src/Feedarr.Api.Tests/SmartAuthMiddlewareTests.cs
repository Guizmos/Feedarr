using System.Net;
using System.Text;
using Feedarr.Api.Data;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Feedarr.Api.Tests;

public sealed class AuthenticationMiddlewareTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.5")]
    public async Task NoPrivateNetworkOrBootstrapExemption(string ip)
    {
        using var f = new Fixture();
        Assert.Equal(401, await f.Request(ip));
        Assert.Equal(401, await f.Request(ip, secret: "fixture-secret"));
        Assert.Equal(200, await f.Request(ip, basic: true));
    }

    [Fact]
    public async Task HttpsOnlyAndRecoveryStillRequireCredentials()
    {
        using var f = new Fixture(requireHttps:true);
        Assert.Equal(403, await f.Request("127.0.0.1", basic:true));
        Assert.Equal(401, await f.Request("127.0.0.1", recovery:true));
        Assert.Equal(200, await f.Request("127.0.0.1", basic:true, recovery:true));
    }

    [Fact]
    public async Task RenamingAdministratorInvalidatesCachedBasicCredentials()
    {
        using var f = new Fixture(); Assert.Equal(200, await f.Request("127.0.0.1", basic:true));
        var security=f.Settings.GetSecurity(new()); security.Username="new-admin"; f.Settings.SaveSecurity(security); f.Cache.Remove(SecuritySettingsCache.CacheKey);
        Assert.Equal(401, await f.Request("127.0.0.1", basic:true));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory=Path.Combine(Path.GetTempPath(),"feedarr-transport-auth-"+Guid.NewGuid().ToString("N"));
        public MemoryCache Cache {get;}=new(new MemoryCacheOptions());
        public SettingsRepository Settings {get;}
        private readonly SetupStateService setup;
        public Fixture(bool requireHttps=false)
        {
            var db=new Db(OptionsFactory.Create(new AppOptions {DataDir=directory}));
            new MigrationsRunner(db,NullLogger<MigrationsRunner>.Instance).Run();
            Settings=new(db); setup=new(Settings,Cache);
            var (hash,salt)=CredentialValidator.HashPassword("Fixture-Password42!");
            Settings.SaveSecurity(new SecuritySettings {Username="admin",PasswordHash=hash,PasswordSalt=salt,RequireHttps=requireHttps});
        }
        public async Task<int> Request(string ip,bool basic=false,string? secret=null,bool recovery=false)
        {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["App:Security:BootstrapSecret"]="fixture-secret",["Security:AllowHttpRecovery"]=recovery.ToString()
            }).Build();
            var context=new DefaultHttpContext();context.Request.Scheme="http";context.Request.Path="/api/sources";context.Connection.RemoteIpAddress=IPAddress.Parse(ip);context.Response.Body=new MemoryStream();
            if(basic)context.Request.Headers.Authorization="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:Fixture-Password42!"));
            if(secret is not null)context.Request.Headers[AuthenticationPolicy.BootstrapSecretHeader]=secret;
            var middleware=new BasicAuthMiddleware(c=>{c.Response.StatusCode=200;return Task.CompletedTask;},config);
            await middleware.InvokeAsync(context,Settings,Cache,setup,new BootstrapTokenService(),new AuthThrottleService(new BasicAuthThrottleOptions(),TimeProvider.System),NullLogger<BasicAuthMiddleware>.Instance);
            return context.Response.StatusCode;
        }
        public void Dispose(){Cache.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
}
