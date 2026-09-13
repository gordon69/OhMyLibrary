# Steam on-disk formats — verified notes

Everything here was verified against a live Steam install on 2026-09-10
(Steam client build shipping `appinfo.vdf` v29). Where this contradicts older
blog posts or the original `ohmylibrary-plan.md`, this file wins — it was
checked against real bytes.

## Locating Steam

| Source | Value seen |
|---|---|
| `HKCU\Software\Valve\Steam` → `SteamPath` | `c:/program files (x86)/steam` |
| `HKCU\Software\Valve\Steam` → `SteamExe` | `c:/program files (x86)/steam/steam.exe` |
| `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` → `InstallPath` | `C:\Program Files (x86)\Steam` |

**Gotcha:** `HKCU` returns *forward slashes and lower case*. Normalise with
`Path.GetFullPath` before comparing or combining. `HKLM\...\WOW6432Node` returns a
normal Windows path and is the better fallback. Both keys can be missing — Steam
may not be installed at all, which must degrade to an empty library, not a crash.

## `steamapps/libraryfolders.vdf`

Text KeyValues1. Present at **two** paths; prefer `steamapps/`, fall back to `config/`:

- `<steam>/steamapps/libraryfolders.vdf`  ← canonical
- `<steam>/config/libraryfolders.vdf`

Shape (index-keyed, not an array):

```
"libraryfolders"
{
    "0"
    {
        "path"        "C:\\Program Files (x86)\\Steam"
        "label"       ""
        "contentid"   "1234567890123456789"
        "totalsize"   "0"
        "apps"
        {
            "228980"  "1029919928"     // appid -> size on disk
        }
    }
    "1"
    {
        "path"        "A:\\SteamLibrary"
        ...
    }
}
```

Notes:
- `path` is the **library root**, so manifests live at `<path>/steamapps/appmanifest_*.acf`
  and games at `<path>/steamapps/common/<installdir>`.
- Paths are escaped (`\\`). ValveKeyValue unescapes them.
- The `apps` block is Steam's own index and can lag reality. Treat the `.acf`
  files on disk as truth; `apps` is only useful as a hint.
- A listed library may be an unplugged drive — every folder needs an existence
  check before enumeration.

## `steamapps/appmanifest_<appid>.acf`

Text KeyValues1, root key `AppState`. Fields we use:

| Key | Type | Notes |
|---|---|---|
| `appid` | int | |
| `name` | string | |
| `StateFlags` | int bitfield | see below |
| `installdir` | string | folder name under `steamapps/common`, **not** a full path |
| `SizeOnDisk` | int64 | |
| `BytesToDownload` / `BytesDownloaded` | int64 | download progress while busy |
| `BytesToStage` / `BytesStaged` | int64 | |
| `buildid` / `TargetBuildID` | int | differ when an update is pending |
| `LastOwner` | uint64 | SteamID64 of the owning account |
| `lastupdated` / `LastPlayed` | unix seconds | `0` means never |
| `InstalledDepots` | nested | not needed for the launcher |

### `StateFlags` bits

```
1     Uninstalled          256   UpdateRunning
2     UpdateRequired       512   UpdatePaused
4     FullyInstalled      1024   UpdateStarted
8     Encrypted           2048   Uninstalling
16    Locked              4096   BackupRunning
32    FilesMissing       65536   Reconfiguring
64    AppRunning        131072   Validating
128   FilesCorrupt      262144   AddingFiles
                        524288   Preallocating
                       1048576   Downloading
                       2097152   Staging
                       4194304   Committing
                       8388608   UpdateStopping
```

`4` alone = healthy installed app. `6` = installed but update required.
Anything in the 256..8388608 range means Steam is actively working on the app,
so the card should show progress rather than a Play button.

**Non-games appear here too.** `228980` "Steamworks Common Redistributables" is a
redistributable, not a game — filter it (and other `type != game` entries) using
`appinfo.vdf`'s `common/type`.

## `appcache/appinfo.vdf` — binary, v29

The single most valuable local file: name, type, genres, store tags, categories,
localised names, release info for **every app the client knows about** (2867 on
the test machine). No network needed.

`ValveKeyValue` parses the *inner* KV blob but **not** the outer container — that
loop has to be hand-written. Verified working structure:

```
uint32  magic            0x07564429 ('29DV') for v29, 0x07564428 for v28
uint32  universe         1
int64   stringTableOffset      // v29 ONLY — absent in v28

// then, repeating until appid == 0:
uint32  appid                  // 0 terminates the file
uint32  size                   // bytes of this record after this field
uint32  infoState
uint32  lastUpdated            // unix seconds
uint64  picsToken
byte[20] sha1TextVdf
uint32  changeNumber
byte[20] sha1BinaryVdf         // v28+ ONLY
byte[]  binaryKeyValues        // fills the rest of `size`

// at stringTableOffset (v29 only):
int32   count
count × null-terminated UTF-8 strings
```

Parse the string table **first**, seek back, then walk the records.
Feed it to ValveKeyValue via `new KVSerializerOptions { StringTable = table }`
with `KVSerializationFormat.KeyValues1Binary`. On the test file the table held
14001 strings starting `appinfo, appid, public_only, common, name, type, ...`.

Always `stream.Position = recordStart + size` after each record rather than
trusting how many bytes the KV parser consumed — that keeps one malformed app
from desyncing the whole file.

### Fields under `common`

```
name              "Cyberpunk 2077"
type              "Game" | "game" | "DLC" | "Demo" | "Tool" | "Application" | ...   (compare case-insensitively!)
genres            { "0" "3" }                     // ordered list -> genre ids
store_tags        { "0" "4115"  "1" "1695" ... }  // ordered list -> tag ids, most relevant first
category          { "category_2" "1" ... }        // store categories, key-suffixed
name_localized    { "russian" "..." "schinese" "..." }
oslist, releasestate, steam_release_date, controller_support, metacritic_score
```

Observed on real apps:

- `570` Dota 2 — `type=game`, genres `1,2,37`, 20 store tags
- `1091500` Cyberpunk 2077 — `type=Game`, genres `3`, tags `4115,1695,6650,...`
- `292030` The Witcher 3 — `type=Game`, genres `3`

`type` casing is inconsistent between apps (`game` vs `Game`) — always compare
with `StringComparison.OrdinalIgnoreCase`.

## Genre and tag names — `localization.vdf` does not exist

The original plan assumed a `localization.vdf` mapping ids to names. **There is
no such file in the modern client.** Verified: the only localisation assets are
`steamui/localization/*-json.js` (UI strings keyed by name, e.g.
`FilterElement_GenreAction`, not by id) and `controller_base/localization`.

Working sources instead:

1. **Store tags** — `https://store.steampowered.com/tagdata/populartags/<language>`
   returns `[{"tagid":492,"name":"Indie"}, ...]`. 429 entries for both `english`
   and `russian`; it resolved every tag id observed on real apps. No API key.
   Cache it — it changes rarely (a week's TTL is plenty).

2. **Genres** — a small fixed Valve list, best shipped as a built-in table
   (`1=Action, 2=Strategy, 3=RPG, 4=Casual, 9=Racing, 18=Sports, 23=Indie,
   25=Adventure, 28=Simulation, 29=Massively Multiplayer, 37=Free To Play, ...`).
   Localised display names can be lifted from `steamui/localization/steamui_<lang>-json.js`
   keys `FilterElement_Genre<Name>` as an enhancement.

Any id that resolves to nothing must still render — fall back to `#<id>` rather
than dropping the tag.

## `appcache/librarycache/` — new hashed layout

The original plan's `<appid>_library_600x900.jpg` naming is **gone**. Current
layout, verified:

```
appcache/librarycache/
  570/
    0bbb630d63262dd66d2fdd0f7d37e8661a410075.jpg              32x32   -> icon
    logo.png                                                  640x137 -> logo (loose)
    06918d1009cc79ffdee853f1ef789cddf8014226/
      library_hero.jpg                                       1920x620
      library_hero_blur.jpg                                   192x62
    4f0854d4f0b7f620515e390628427f9f37335ffd/
      library_header.jpg                                      460x215
    6843027380c3bfd0952449fd9174f492ef2e7b40/
      library_capsule.jpg                                     300x450  <- THE GRID COVER
    c9e9902151b7e9049145921c09a1aa4e6744530b/
      markers.svg
```

Rules:
- The hash subfolder name is opaque — **glob for the well-known file name**,
  don't try to predict the hash: `librarycache/<appid>/*/library_capsule.jpg`.
- A loose `<sha1>.jpg` directly under `<appid>/` is the 32×32 icon.
- Not every app has every asset. Fall back in this order for a grid cover:
  `library_capsule.jpg` → `library_header.jpg` → CDN → generated placeholder.
- `appcache/librarycache/assetcache.vdf` also indexes these, but globbing is
  simpler and does not depend on that file's undocumented shape.

CDN fallbacks (no key required):

```
https://cdn.cloudflare.steamstatic.com/steam/apps/<appid>/library_600x900.jpg
https://cdn.cloudflare.steamstatic.com/steam/apps/<appid>/header.jpg
```

## `config/loginusers.vdf`

Text KV, gives the SteamID64 of accounts that have signed in on this machine —
lets the app auto-detect the user instead of asking them to type an id.

```
"users"
{
    "76561198000000001"
    {
        "AccountName"   "..."
        "PersonaName"   "..."
        "MostRecent"    "1"
        "Timestamp"     "1789026215"
    }
}
```

`userdata/<accountId>/` uses the **32-bit** account id, i.e. `steamId64 & 0xFFFFFFFF`.

`userdata/<accountId>/config/librarycache/<appid>.json` holds UI cache
(achievement progress, trading-card badge state) — not asset paths. Only worth
reading if achievements ever become a feature.

## `steam://` URIs

Launched with `Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })`.

| URI | Effect |
|---|---|
| `steam://rungameid/<appid>` | launch (installs first if missing) |
| `steam://install/<appid>` | install, or resume/repair an existing install |
| `steam://uninstall/<appid>` | uninstall prompt |
| `steam://validate/<appid>` | verify integrity |
| `steam://nav/games/details/<appid>` | open the app's library page in Steam |
| `steam://store/<appid>` | open the store page |
| `steam://friends/add/<steamid64>` | friend actions |

App ids come from a trusted local parse, but the value must still be validated
as a positive integer before it is concatenated into a URI.

## Web API endpoints used

All under `https://api.steampowered.com`, all need `?key=<apikey>`:

- `IPlayerService/GetOwnedGames/v1/` — `include_appinfo=1&include_played_free_games=1`
- `ISteamUser/GetFriendList/v1/` — `relationship=friend`
- `ISteamUser/GetPlayerSummaries/v2/` — up to **100** `steamids` per call, comma separated

Reality checks that the code must handle:
- A private profile returns HTTP 401/403 for `GetFriendList`, and an **empty
  `response` object** (not an error) for a friend's `GetOwnedGames`. Empty means
  "hidden", not "owns nothing" — surface that distinction in the UI.
- Rate limiting is real; friend fan-out must be throttled and cached, not run on
  every window focus.
- No API key configured is a normal state: installed games still work fully from
  local files. Owned-but-not-installed and friends simply stay empty.
