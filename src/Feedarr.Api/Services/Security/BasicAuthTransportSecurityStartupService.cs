using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models.Settings;
using Feedarr.Api.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Feedarr.Api.Services.Security;

public sealed class BasicAuthTransportSecurityStartupService : IHostedService
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<BasicAuthTransportSecurityOptions> _transportOptions;
    private readonly IOptions<ForwardedHeadersOptions> _forwardedHeadersOptions;
    private readonly IConfiguration _configuration;
    private readonly SettingsRepository _settingsRepository;
    private readonly ILogger<BasicAuthTransportSecurityStartupService> _log;

    public BasicAuthTransportSecurityStartupService(
        IHostEnvironment environment,
        IOptions<BasicAuthTransportSecurityOptions> transportOptions,
        IOptions<ForwardedHeadersOptions> forwardedHeadersOptions,
        IConfiguration configuration,
        SettingsRepository settingsRepository,
        ILogger<BasicAuthTransportSecurityStartupService> log)
    {
        _environment = environment;
        _transportOptions = transportOptions;
        _forwardedHeadersOptions = forwardedHeadersOptions;
        _configuration = configuration;
        _settingsRepository = settingsRepository;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateOrThrow(
            _environment,
            _transportOptions.Value,
            _forwardedHeadersOptions.Value,
            _configuration,
            _settingsRepository,
            _log);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void ValidateOrThrow(
        IHostEnvironment environment,
        BasicAuthTransportSecurityOptions transportOptions,
        ForwardedHeadersOptions forwardedHeadersOptions,
        IConfiguration configuration,
        SettingsRepository settingsRepository,
        ILogger log)
    {
        if (environment.IsDevelopment())
            return;

        var requireHttps = transportOptions.RequireHttpsForBasicAuth ?? environment.IsProduction();
        if (!requireHttps)
            return;

        var security = settingsRepository.GetSecurity(new SecuritySettings());
        var bootstrapSecret = SmartAuthPolicy.GetBootstrapSecret(configuration);
        var authMode = SmartAuthPolicy.NormalizeAuthMode(security);
        var authConfigured = SmartAuthPolicy.IsAuthConfigured(security, bootstrapSecret);
        if (authMode == "open" || !authConfigured)
            return;

        if (transportOptions.TrustedReverseProxyTls)
        {
            var hasForwardedProto = (forwardedHeadersOptions.ForwardedHeaders & ForwardedHeaders.XForwardedProto) != 0;
            var hasKnownProxy = forwardedHeadersOptions.KnownProxies.Count > 0 || forwardedHeadersOptions.KnownNetworks.Count > 0;
            if (hasForwardedProto && hasKnownProxy)
            {
                log.LogInformation(
                    "Basic Auth startup transport check accepted trusted reverse proxy TLS configuration");
                return;
            }

            throw new InvalidOperationException(
                "Basic Auth requires trusted TLS transport. Security:TrustedReverseProxyTls=true was set, " +
                "but forwarded headers are not safely configured with X-Forwarded-Proto and at least one known proxy/network.");
        }

        var enforceHttps = configuration.GetValue("App:Security:EnforceHttps", false);
        if (enforceHttps)
        {
            log.LogInformation("Basic Auth startup transport check accepted direct HTTPS enforcement");
            return;
        }

        throw new InvalidOperationException(
            "Basic Auth is configured but secure transport is not enforced. " +
            "Set App:Security:EnforceHttps=true for direct TLS, or set Security:TrustedReverseProxyTls=true " +
            "with known proxies/networks when TLS is terminated by a trusted reverse proxy.");
    }
}
