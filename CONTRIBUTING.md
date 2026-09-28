# Contributing to Daxis

Thanks for helping. Daxis aims to stay **small, fast and safe**, so the bar for new features is "does this make everyday Fabric work clearly better?"

## Getting started

```bash
git clone https://github.com/AFetisa/daxis.git
cd daxis
dotnet build Daxis.sln
dotnet test tests/Daxis.Tests
dotnet run --project src/Daxis.App
```

You need the .NET 8 SDK. On Linux you also need `libice6 libsm6 libfontconfig1` (see README).

To run against a clean profile without touching your normal sign-in, set `DAXIS_HOME` to an empty folder.

## Ground rules

- **Cross-platform:** everything must build and run on Windows and Linux. No WPF, WinForms, P/Invoke or Registry.
- **Safety:** any new action that changes something in the service must go through the model review (`ChangeTracker`) or `TabBase.Ask(...)` confirmation. Tokens are only sent through `FabricClient`, which enforces the host allowlist.
- **Design:** follow `DESIGN.md`. Use theme tokens (`DynamicResource`), never hardcoded colours, and keep one green button per view.
- **Tests:** logic lives in `Daxis.Core` and gets an xUnit test. UI code stays thin.
- **Dependencies:** think twice before adding one. The TOM SDK version is pinned on purpose.
- **No data in issues or PRs:** never paste tokens, tenant IDs, connection strings or real model content.

## Pull requests

1. Open an issue first for anything non-trivial, so we can agree on the approach.
2. Keep PRs focused. Describe what and why, and include a screenshot for UI changes.
3. CI must pass on Windows and Linux.

## Contribution terms

Daxis is source-available under the [Daxis Community License](LICENSE). Changing the code is only permitted to prepare a contribution to this repository.

By opening a pull request you:
- confirm you have the right to submit the work;
- grant the author a perpetual, irrevocable licence to use, modify, distribute and relicense your contribution (licence section 6);
- agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

Contributing doesn't give you extra rights to use Daxis beyond the licence.
