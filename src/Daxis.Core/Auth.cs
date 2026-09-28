using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace Daxis.Core;

public static class AppPaths
{
    /// <summary>Token cache and settings. <c>DAXIS_HOME</c> overrides it (portable installs, isolated test runs).</summary>
    public static string Data { get; } = Create(
        Environment.GetEnvironmentVariable("DAXIS_HOME") is { Length: > 0 } home ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daxis"));

    static string Create(string dir)
    {
        var info = Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) // owner-only on Linux/macOS: the folder holds the token cache
            File.SetUnixFileMode(info.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return info.FullName;
    }
}

/// <summary>
/// One MSAL public client for everything. A Power BI-audience token is accepted by the
/// Power BI REST API, the Fabric REST API and the XMLA endpoint; OneLake needs a storage token.
/// </summary>
public sealed class Auth
{
    // Default: the Power BI Desktop public client, which needs no app registration in most tenants.
    // Organisations can point Daxis at their own Entra app (public client, redirect http://localhost) with DAXIS_CLIENT_ID.
    const string DefaultClientId = "ea0616ba-638b-4df5-95b9-636659ae5121";
    static readonly string ClientId = Environment.GetEnvironmentVariable("DAXIS_CLIENT_ID") is { Length: > 0 } id ? id : DefaultClientId;
    public const string PowerBi = "https://analysis.windows.net/powerbi/api/.default";
    public const string Storage = "https://storage.azure.com/.default";

    readonly IPublicClientApplication _app;
    public IAccount? Account { get; private set; }

    public Auth()
    {
        _app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .WithRedirectUri("http://localhost") // system browser loopback; works on Windows and Linux
            .Build();
        TokenCacheFile.Bind(_app.UserTokenCache, Path.Combine(AppPaths.Data, "msal.cache"));
    }

    /// <summary>Silently restores the last session from the token cache.</summary>
    public async Task<bool> TryRestoreAsync()
    {
        Account = (await _app.GetAccountsAsync()).FirstOrDefault();
        if (Account is null) return false;
        try { await AcquireAsync(); return true; }
        catch (MsalUiRequiredException) { Account = null; return false; }
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        var r = await _app.AcquireTokenInteractive([PowerBi])
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct);
        Account = r.Account;
    }

    public async Task SignOutAsync()
    {
        foreach (var a in await _app.GetAccountsAsync()) await _app.RemoveAsync(a);
        Account = null;
    }

    public async Task<AuthenticationResult> AcquireAsync(string scope = PowerBi)
    {
        if (Account is null) throw new InvalidOperationException("Sign in first.");
        try { return await _app.AcquireTokenSilent([scope], Account).ExecuteAsync(); }
        catch (MsalUiRequiredException) when (scope != PowerBi)
        {
            // First use of a secondary resource (e.g. OneLake) may need consent.
            return await _app.AcquireTokenInteractive([scope]).WithAccount(Account)
                .WithUseEmbeddedWebView(false).ExecuteAsync();
        }
    }

    public async Task<string> TokenAsync(string scope = PowerBi) => (await AcquireAsync(scope)).AccessToken;
}

/// <summary>Persists the MSAL cache: DPAPI on Windows, owner-only file elsewhere.</summary>
static class TokenCacheFile
{
    // ponytail: Linux cache is a 0600 file in a 0700 folder, not libsecret; add Microsoft.Identity.Client.Extensions.Msal if a keyring is required.
    public static void Bind(ITokenCache cache, string path)
    {
        cache.SetBeforeAccess(a =>
        {
            if (!File.Exists(path)) return;
            try { a.TokenCache.DeserializeMsalV3(Unprotect(File.ReadAllBytes(path))); }
            catch (IOException) { } // another instance is writing; the next access retries
            catch (Exception e) when (e is CryptographicException or MsalClientException) { File.Delete(path); }
        });
        cache.SetAfterAccess(a =>
        {
            if (!a.HasStateChanged) return;
            var bytes = Protect(a.TokenCache.SerializeMsalV3());
            // Write-then-rename so a second instance never reads a half-written file.
            var tmp = $"{path}.{Environment.ProcessId}.tmp";
            if (OperatingSystem.IsWindows()) File.WriteAllBytes(tmp, bytes);
            else
            {
                using (var fs = new FileStream(tmp, new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                    fs.Write(bytes);
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException) { File.Delete(tmp); } // lost a race with another instance; its copy is as fresh
        });
    }

    static byte[] Protect(byte[] b) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(b, null, DataProtectionScope.CurrentUser) : b;

    static byte[] Unprotect(byte[] b) =>
        OperatingSystem.IsWindows() ? ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser) : b;
}
