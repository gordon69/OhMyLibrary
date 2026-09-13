# Test fixtures

The regression net for the parsers: Valve changes these formats between client builds, and a
fixture that no longer parses is the first warning we get.

Provenance differs per fixture and is stated honestly in each section below — some files are
scrubbed captures of small text files, the binary containers are invented outright. Two rules hold
everywhere, because this project is meant to go open source:

- **No real account data.** Every SteamID64 here is `76561198000000001` or `…002`, every account and
  persona name is obviously fake, and no real library path, API key or PICS token is committed.
- **Nothing is copied out of `appcache/`.** The binary `appinfo.vdf` fixtures are synthesised rather
  than trimmed out of a client; see the last section for why. Anything copied from a live install
  must be scrubbed before it is committed, and some things cannot be scrubbed enough to commit
  at all.

## `Acf/` — `steamapps/appmanifest_<appid>.acf`

Text KeyValues. The first three are **real captures**: small manifests from a live client, carrying
nothing but an app id, a name, an install folder and byte counters, with `LastOwner` replaced by a
fake SteamID64. The rest are hand-authored.

| File | Where it came from | What it covers |
|---|---|---|
| `appmanifest_570.acf` | live client | a healthy install, `StateFlags 4`, `LastPlayed 0` |
| `appmanifest_1091500.acf` | live client | a played install, non-zero `LastPlayed`, DLC depots |
| `appmanifest_228980.acf` | live client | the Steamworks redistributable that must never reach the grid |
| `appmanifest_900001.acf` | hand-authored | `StateFlags 6`: installed, update required, `TargetBuildID` ahead |
| `appmanifest_900002.acf` | hand-authored | `StateFlags 1049606`: downloading, with transfer counters |
| `appmanifest_900003.acf` | hand-authored | a stale manifest whose install folder is not on disk |
| `appmanifest_900004.acf` | hand-authored | not a key-values document at all |
| `appmanifest_900005.acf` | hand-authored | no `name`, no `StateFlags` |

## `LibraryFolders/` and `LoginUsers/`

Valve's shape, everything machine-specific replaced.

`libraryfolders.vdf` keeps the three-folder structure a real file has, but the paths are
`__LIB0__`, `__LIB1__` and `__LIB2__`, the `contentid` values are repeated digits, and the `apps`
maps are invented — a real one is a complete inventory of what its owner has installed, with sizes,
which is a fingerprint rather than a format. Only app `570` and the redistributable `228980` keep
their real ids, because two tests read them by id; their byte counts are arbitrary. The reader drops
folders that do not exist, so the tests substitute real temp directories at run time
(`FakeSteam.WriteLibraryFolders`) and escape the backslashes the way Valve does. `__MISSING__` is
deliberately left pointing at a path that is never created.

`loginusers.vdf` is the live shape with fake ids and names, and carries **no** `MostRecent` key —
the case that breaks auto-detection by that flag alone. `loginusers_multi.vdf` adds the flag plus an
entry whose key is not a SteamID64.

## `AppInfo/` — the binary container, invented end to end

**Nothing in this folder came off a real machine.** A live `appcache/appinfo.vdf` is around 8 MB and
holds, well beyond bare app names: publisher and developer strings, EULA titles and store EULA URLs,
Metacritic affiliate links, around 25 localised store names per app, per-branch marketing copy, full
depot and manifest ids with per-language byte sizes, and a `cegpublickey` blob — Valve's Custom
Executable Generation signing key. Zeroing the PICS token and the two SHA-1 fields, which is all a
trimmed capture needs to be account-safe, does not touch any of that. So these files are minted from
invented records instead:

| File | What it is |
|---|---|
| `appinfo_v29.vdf` | 6 442 bytes in the format the current client writes: a 111-entry string table and four records — apps `9000101`, `9000202`, `9000303`, `9000404` |
| `appinfo_v29_truncated.vdf` | the same bytes cut off exactly where the string table starts, so the offset in the header points at the end of the file and no record key can resolve |
| `appinfo_v28.vdf` | the older format, which carries no string table: apps `770`, `771`, `772` |
| `appinfo_v28_truncated.vdf` | the same bytes cut mid-record, which must still yield everything read before the cut |
| `appinfo_garbage.vdf` | random bytes with a leading `{`; not a container at all |

Every name in both containers is fiction and starts with `Fixture`. The v29 file goes further: its
app ids sit far above the range Steam actually issues and its genre, store tag and category ids are
numbers Valve does not use, so no value in it can be mistaken for a capture. (The older v28 records
still carry Valve-shaped genre and tag ids — `1`, `2`, `37`, `4115` — which are facts about the
format rather than anyone's content; regenerate them the same way if that ever needs to change.)

What is copied from reality is the *shape*, which is what the parser cares about: the v29 flagship
record carries a full `common` block — name, lower-case `type`, an
out-of-order genre list, twenty relevance-ordered store tags, `category_<id>` keys, 29 localised
names, an `associations` list whose first entry is a franchise rather than the developer,
`steam_release_date`, `metacritic_score`, `oslist` and `sortas` — plus the nested and sibling blocks
a real record carries around them (`library_assets_full`, `supported_languages`, `extended`,
`config/launch`, `ufs`, `localization/richpresence`), which is what fills the string table.

Regenerate them with:

```
set OHMYLIBRARY_FIXTURE_DIR=<repo>\OhMyLibrary.Tests\Fixtures\AppInfo
dotnet test OhMyLibrary.Tests --filter FullyQualifiedName~AppInfoFixtureRegeneration
```

The records live in `Tools/AppInfoFixtureRegeneration.cs` and the container writer in
`Tools/AppInfoFixtureBuilder.cs`; without that environment variable the test that drives them skips.
Steam is not required — there is nothing to copy from. Only `appinfo_garbage.vdf` differs between
runs, because it is random by definition.

Because the fixtures are invented, they can only prove that the reader still reads *this* shape.
What proves the shape is still Valve's is `AppInfoReaderTests.ReadAll_StillParsesTheLiveContainerOnThisMachine`,
which parses this machine's own `appinfo.vdf` when Steam is installed and asserts the format
invariants against it. That is the right home for real data: it reads it and commits nothing.
