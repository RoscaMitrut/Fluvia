using GitHubJwt;
using Octokit;

namespace FluviaBot.Services;

/// <summary>
/// Generates a short-lived installation access token so we can post comments
/// on behalf of the GitHub App installation (mirrors how octokit/app does it in Node).
/// </summary>
public class GitHubAppTokenProvider
{
    private readonly IConfiguration _config;
    private readonly ILogger<GitHubAppTokenProvider> _logger;

    public GitHubAppTokenProvider(IConfiguration config, ILogger<GitHubAppTokenProvider> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns an Octokit client authenticated for the given installation.
    /// </summary>
    public async Task<GitHubClient> GetInstallationClientAsync(long installationId)
    {
        // AppIntegrationId is an int. Parse as int and give a clear error on a
        // missing / non-numeric / out-of-range value rather than a bare
        // FormatException or OverflowException (#13).
        var rawAppId = _config["GitHub:AppId"];
        if (!int.TryParse(rawAppId, out var appId) || appId < 1)
            throw new InvalidOperationException(
                $"GitHub:AppId must be a positive numeric GitHub App ID, but got: '{rawAppId ?? "<null>"}'.");

        var keyPath = _config["GitHub:PrivateKeyPath"]
                      ?? throw new InvalidOperationException(
                          "GitHub:PrivateKeyPath is not configured.");

        if (!File.Exists(keyPath))
            throw new FileNotFoundException(
                $"GitHub App private key not found at '{keyPath}'. " +
                "Check the volume mount and the GitHub__PrivateKeyPath value.", keyPath);

        // Build a JWT signed with the app's private key.
        // Reading straight from the .pem file avoids all string-escaping issues.
        var generator = new GitHubJwtFactory(
            new FilePrivateKeySource(keyPath),
            new GitHubJwtFactoryOptions
            {
                AppIntegrationId = appId,
                ExpirationSeconds = 600
            });

        var jwt = generator.CreateEncodedJwtToken();

        // Exchange the JWT for an installation access token
        var appClient = new GitHubClient(new ProductHeaderValue("FluviaBot"))
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer)
        };

        var tokenResponse = await GitHubRetry.ExecuteAsync(
            () => appClient.GitHubApps.CreateInstallationToken(installationId),
            _logger,
            operation: "CreateInstallationToken");

        return new GitHubClient(new ProductHeaderValue("FluviaBot"))
        {
            Credentials = new Credentials(tokenResponse.Token)
        };
    }
}