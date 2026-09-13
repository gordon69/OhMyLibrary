# OhMyLibrary

A custom Windows launcher layered on top of Steam: our own library view,
collections and tags, built from data read locally plus the Steam Web API.

- **Roadmap / stages:** [ohmylibrary-plan.md](ohmylibrary-plan.md)
- **Verified Steam file formats:** [docs/steam-formats.md](docs/steam-formats.md) —
  read this before touching anything under `Vdf/` or `Steam/`. It was checked
  against a live install and corrects several assumptions in the original plan
  (there is no `localization.vdf`; `librarycache` uses hashed subfolders;
  `appinfo.vdf` v29 needs a hand-written container loop).

## Layout

```
OhMyLibrary.App/    WPF + WPF-UI (Fluent). Views, ViewModels, DI host, theme.
OhMyLibrary.Core/   Domain models, Steam parsers and clients, services. No UI, no SQL.
OhMyLibrary.Data/   SQLite schema and Dapper repositories. No UI, no Steam knowledge.
OhMyLibrary.Tests/  xUnit. Parser tests run against fixtures in Tests/Fixtures.
```

Dependency direction is strictly `App -> Data -> Core`. Core references neither
of the others and must stay free of WPF types.

## Conventions

- .NET 10, `net10.0-windows`, x64 only. Nullable is on and `nullable` warnings
  are errors — do not silence them with `!` unless the invariant is genuinely local.
- NuGet versions live in `Directory.Packages.props` (central package management).
  `PackageReference` in a `.csproj` carries **no** `Version` attribute.
- MVVM via `CommunityToolkit.Mvvm` source generators: `[ObservableProperty]`,
  `[RelayCommand]`, `ObservableObject`. No hand-written `INotifyPropertyChanged`.
- Async all the way down, every public async method takes a `CancellationToken`.
- Services are registered in the DI container in `App.xaml.cs`; ViewModels get
  their dependencies injected, never resolved from a static locator.

## Non-negotiables

- **Graceful degradation.** No Steam client, no libraries, an unplugged library
  drive, a private profile, no API key — each of these is a normal state that
  renders an empty or partial view. None of them may throw out of a service.
- **Never write into Steam's directories.** Everything we read there is read-only.
  Our own state lives in `%LOCALAPPDATA%/OhMyLibrary/`.
- **Parsers are tested against fixtures from day one.** Valve changes these
  formats between client builds; the fixtures are the regression net.
- **Secrets stay out of the repo.** The Steam Web API key is read from
  `appsettings.local.json` (gitignored) or, once the app has run, from
  `%LOCALAPPDATA%/OhMyLibrary/user-settings.json`. .NET user secrets are **not**
  wired up — do not tell contributors to put a key there. Never log it, never
  put it in a URL that gets logged.
- Cache cadence differs by source: local files are cheap (rescan on focus,
  throttled), Web API is expensive (hours-long TTL, or manual refresh).

## Build

```
dotnet build OhMyLibrary.sln
dotnet test  OhMyLibrary.sln
dotnet run   --project OhMyLibrary.App
```
