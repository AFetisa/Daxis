# Security policy

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through GitHub:
**Security → Report a vulnerability** on this repository ([direct link](https://github.com/AFetisa/daxis/security/advisories/new)).

Include what you found, how to reproduce it, and the impact you see. You'll get an acknowledgement within 5 business days. We aim to ship a fix or mitigation within 30 days for high-severity issues, and to credit you in the release notes (unless you'd rather stay anonymous).

## Supported versions

Only the latest release and `main` receive security fixes.

## Threat model in brief

Daxis is a local desktop client acting on behalf of the signed-in user. Its main security properties:

| Property | How |
|---|---|
| Tokens never leave Microsoft endpoints | `FabricClient.IsTrusted` allowlists hosts per token audience; anything else is refused before a request is made |
| Tokens at rest are protected | MSAL cache is DPAPI-encrypted (Windows) or an owner-only file in an owner-only folder (Linux); writes are atomic |
| No silent writes to the service | Model edits need a reviewed before/after list; refresh and notebook save/run need explicit confirmation; notebook save refuses to overwrite remote changes |
| Edits target the right model | XMLA connections match the model by ID, and items always open against their own workspace |
| No arbitrary URL launch | Only `https` Fabric / Power BI portal links and the product pages (this repository, portable-labs.com) reach the OS launcher |
| Supply chain | Locked NuGet restore, pinned GitHub Actions (by SHA), least-privilege CI tokens, release checksums and build provenance |

Out of scope: a compromised local machine or user account, and Microsoft service behaviour.

## Where Daxis connects

| Host | What | Why |
|---|---|---|
| `login.microsoftonline.com` | Sign-in in your browser (PKCE) | Authentication |
| `api.fabric.microsoft.com`, `api.powerbi.com`, `*.analysis.windows.net` | REST and XMLA with your token | Fabric, Power BI and semantic models |
| `onelake.dfs.fabric.microsoft.com` | Storage-scoped token only | Lakehouse files |

The token cache lives in `%LocalAppData%\Daxis` (Windows) or `~/.local/share/Daxis` (Linux). Signing out clears it.

## Using your own sign-in app

By default Daxis signs in with the public client ID used by Power BI Desktop, which works in most tenants without setup. For managed environments, register a *public client* app with redirect URI `http://localhost` and delegated Power BI Service permissions, then start Daxis with:

```bash
DAXIS_CLIENT_ID=<your-app-id> ./run.sh                   # Linux, Git Bash
$env:DAXIS_CLIENT_ID="<your-app-id>"; .\run.ps1         # PowerShell
```

`DAXIS_HOME` moves the data folder, for example for a portable install on a USB drive.
