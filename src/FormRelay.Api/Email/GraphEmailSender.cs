using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using System.Security.Cryptography.X509Certificates;

namespace FormRelay.Api.Email;

/// <summary>
/// Sends mail through Microsoft Graph (POST sendMail). Two ways to authenticate, chosen by config:
/// <list type="bullet">
/// <item><b>Delegated (demo):</b> no client secret. You sign in once as the sender, with a device code (the code
/// and link are written to the app log) or, when <c>Graph:SignIn</c> is "Browser", in a browser window that opens on this
/// machine (authorization code flow with PKCE). The sign-in is cached for later runs. Mail is sent from that account.</item>
/// <item><b>App-only (production):</b> <c>Graph:CertificateThumbprint</c> (preferred; certificate in the server's
/// LocalMachine\My store) or <c>Graph:CertificatePath</c>, or else <c>Graph:ClientSecret</c> is set. The app sends as
/// <c>Graph:Sender</c> (a shared mailbox), which should be locked down with an Exchange application access policy.</item>
/// </list>
/// </summary>
public class GraphEmailSender(IConfiguration config, IHttpClientFactory http, ILogger<GraphEmailSender> log) : IEmailSender
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config["Graph:TenantId"]) && !string.IsNullOrWhiteSpace(config["Graph:ClientId"])
        && !string.IsNullOrWhiteSpace(config["Graph:Sender"]);

    private static readonly string[] DelegatedScopes = ["https://graph.microsoft.com/Mail.Send"];
    private static readonly string[] AppScopes = ["https://graph.microsoft.com/.default"];

    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenCredential? credential;
    private bool appOnly;

    public async Task SendAsync(string toAddresses, string subject, string body, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);

        var url = appOnly
            ? $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(config["Graph:Sender"] ?? throw new InvalidOperationException("Graph:Sender is required for app-only sending"))}/sendMail"
            : "https://graph.microsoft.com/v1.0/me/sendMail";

        var recipients = toAddresses
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new { emailAddress = new { address = a } })
            .ToList();

        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = "Text", content = body },
                toRecipients = recipients
            },
            saveToSentItems = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.CreateClient().SendAsync(request, ct);

        // Graph answers 202 Accepted when it takes the message.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Graph sendMail failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            credential ??= await CreateCredentialAsync(ct);
            var token = await credential.GetTokenAsync(new TokenRequestContext(appOnly ? AppScopes : DelegatedScopes), ct);
            return token.Token;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Finds the certificate with its private key. A thumbprint is looked up in the machine's Personal store
    /// (LocalMachine\My), which is where the server's certificate lives; the app pool identity needs read access to
    /// the private key. A .pfx path (with <c>Graph:CertificatePassword</c> if it has one) works for development.
    /// </summary>
    private X509Certificate2 LoadCertificate(string? thumbprint, string? path)
    {
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            var wanted = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
            foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, wanted, validOnly: false);
                if (found.Count == 0) continue;
                var cert = found[0];
                if (!cert.HasPrivateKey)
                    throw new InvalidOperationException($"Certificate {wanted} is in {location}\\My but has no private key.");
                try { _ = cert.GetRSAPrivateKey(); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Certificate {wanted} was found but this app can't read its private key. Grant the app pool identity read access to it.", ex);
                }
                log.LogInformation("Graph certificate {Thumbprint} loaded from {Location}\\My, expires {Expires:yyyy-MM-dd}",
                    wanted, location, cert.NotAfter);
                return cert;
            }
            throw new InvalidOperationException($"Certificate with thumbprint {wanted} not found in LocalMachine\\My or CurrentUser\\My.");
        }

        var pfx = X509CertificateLoader.LoadPkcs12FromFile(path!, config["Graph:CertificatePassword"]);
        log.LogInformation("Graph certificate loaded from file, expires {Expires:yyyy-MM-dd}", pfx.NotAfter);
        return pfx;
    }

    private async Task<TokenCredential> CreateCredentialAsync(CancellationToken ct)
    {
        var tenant = config["Graph:TenantId"];
        var client = config["Graph:ClientId"];
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(client))
            throw new InvalidOperationException("Graph:TenantId and Graph:ClientId are required when Email:Provider is Graph");

        // App-only with a certificate (preferred): the app proves itself with the certificate's private key.
        var thumbprint = config["Graph:CertificateThumbprint"];
        var certPath = config["Graph:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(thumbprint) || !string.IsNullOrWhiteSpace(certPath))
        {
            appOnly = true;
            return new ClientCertificateCredential(tenant, client, LoadCertificate(thumbprint, certPath),
                new ClientCertificateCredentialOptions { SendCertificateChain = true });
        }

        // App-only with a client secret.
        var secret = config["Graph:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            appOnly = true;
            return new ClientSecretCredential(tenant, client, secret);
        }

        // Delegated: remember who signed in so later runs are silent.
        appOnly = false;
        var recordPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FormRelay", "graph-auth-record.json");
        AuthenticationRecord? record = null;
        if (File.Exists(recordPath))
        {
            await using var read = File.OpenRead(recordPath);
            record = await AuthenticationRecord.DeserializeAsync(read, ct);
        }

        var cache = new TokenCachePersistenceOptions { Name = "FormRelay" };
        TokenCredential cred;
        var browser = string.Equals(config["Graph:SignIn"], "Browser", StringComparison.OrdinalIgnoreCase);
        if (browser)
        {
            // Authorization code flow with PKCE: a browser window opens on this machine. Needs http://localhost as a
            // redirect URI under "Mobile and desktop applications". Only for a machine with someone at it (the demo).
            if (record is null) log.LogWarning("Microsoft sign-in needed to send email: a browser window is opening on this machine.");
            cred = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = tenant,
                ClientId = client,
                RedirectUri = new Uri("http://localhost"),
                AuthenticationRecord = record,
                TokenCachePersistenceOptions = cache
            });
        }
        else
        {
            cred = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = tenant,
                ClientId = client,
                AuthenticationRecord = record,
                TokenCachePersistenceOptions = cache,
                DeviceCodeCallback = (code, _) =>
                {
                    log.LogWarning("Microsoft sign-in needed to send email. {Message}", code.Message);
                    return Task.CompletedTask;
                }
            });
        }

        if (record is null)
        {
            record = cred switch
            {
                InteractiveBrowserCredential b => await b.AuthenticateAsync(new TokenRequestContext(DelegatedScopes), ct),
                DeviceCodeCredential d => await d.AuthenticateAsync(new TokenRequestContext(DelegatedScopes), ct),
                _ => throw new InvalidOperationException("Unexpected credential type")
            };
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
            await using var write = File.Create(recordPath);
            await record.SerializeAsync(write, ct);
        }
        return cred;
    }
}
