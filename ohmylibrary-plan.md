# OhMyLibrary — план разработки

> **Исторический документ.** Это исходный план, написанный до реализации. Он
> сохранён как есть, чтобы видеть, откуда проект вырос, и местами уже не
> соответствует коду:
>
> - `localization.vdf` в современном клиенте Steam не существует — имена тегов
>   берутся из `store.steampowered.com/tagdata/populartags/<lang>`;
> - обложки лежат не как `<appid>_library_600x900.jpg`, а в хешированных
>   подпапках `librarycache/<appid>/<sha1>/`;
> - `appinfo.vdf` v29 требует своего разбора контейнера, ValveKeyValue парсит
>   только внутренний KV-блоб;
> - `SteamWebApiClient` живёт в `Core/Web/`, а не в `Core/Steam/`.
>
> Актуальное описание форматов — [docs/steam-formats.md](docs/steam-formats.md),
> оно проверено по реальным байтам и имеет приоритет над этим файлом.

Кастомный Windows-лаунчер поверх Steam: своя библиотека, свои коллекции и теги, поверх данных, читаемых локально + через Steam Web API.

## 1. Стек

| Слой | Выбор | Комментарий |
|---|---|---|
| Платформа | .NET 10 (LTS, до ноября 2028) | .NET 8 уходит в maintenance-режим к концу 2026, .NET 9 не LTS — стартовать сразу на 10 |
| UI | WPF + [WPF-UI](https://github.com/lepoco/wpfui) | Fluent-тема, Mica/акрил, минимум кастомной стилизации |
| MVVM | CommunityToolkit.Mvvm | source-generator'ы, почти без boilerplate |
| Парсинг VDF/ACF/appinfo.vdf | [ValveKeyValue](https://github.com/ValveResourceFormat/ValveKeyValue) (NuGet) | закрывает и текстовый, и бинарный формат |
| Локальное хранилище | SQLite (`Microsoft.Data.Sqlite` + Dapper) | без EF Core — схема маленькая, миграции не нужны, `CREATE TABLE IF NOT EXISTS` на старте достаточно |
| HTTP | `HttpClient` + `System.Text.Json` | без сторонних SDK для Steam Web API |
| Запуск игр / установка | `Process.Start(uri, UseShellExecute = true)` | `steam://rungameid/...`, `steam://install/...` |
| Слежка за файлами | `FileSystemWatcher` на `steamapps/` | реакция на изменения `.acf` без опроса по таймеру |

Таргет — только `win-x64`. `win-x86` добавляется позже одной строкой в RID, если вообще понадобится.

## 2. Структура решения

```
OhMyLibrary.sln
├── OhMyLibrary.App/        # WPF, окна, вью, WPF-UI тема
├── OhMyLibrary.Core/       # доменная логика, сервисы, парсеры
│   ├── Vdf/                  # обёртки над ValveKeyValue: Acf, LibraryFolders, AppInfo, Localization
│   ├── Steam/                # SteamPathResolver, SteamUriLauncher, SteamWebApiClient
│   └── Services/             # GameLibraryService, InstallStateService, TagService, FriendsService
├── OhMyLibrary.Data/       # SQLite: схема, репозитории
└── OhMyLibrary.Tests/      # юнит-тесты, особенно на парсеры VDF
```

## 3. Черновая схема SQLite

```sql
Games            (app_id PK, name, is_owned, is_installed, install_dir, size_bytes,
                   state_flags, buildid, last_local_scan_utc)

Genres           (genre_id PK, name, lang)
GameGenres       (app_id, genre_id)

Tags             (tag_id PK, name, lang)
GameTags         (app_id, tag_id)

Friends          (steam_id64 PK, persona_name, avatar_url, last_synced_utc)
FriendGames      (steam_id64, app_id)

Collections      (collection_id PK, name, sort_order)
GameCollections  (app_id, collection_id, sort_order)

SyncMeta         (key PK, last_run_utc)   -- когда в последний раз дёргали GetOwnedGames и т.п.
```

## 4. Этапы

### Этап 0 — Каркас (перед любой фичей)
- Solution/проекты по структуре выше, WPF-UI тема подключена, пустое главное окно рендерится
- `SteamPathResolver`: путь Steam из реестра (`HKCU\Software\Valve\Steam`)
- Чтение `libraryfolders.vdf`
- Хранение Steam API key вне репозитория (`appsettings.local.json` в `.gitignore`, либо user secrets)
- Инициализация SQLite-файла + `CREATE TABLE IF NOT EXISTS`
- Первый коммит: работающее пустое окно в теме — точка отсчёта для регрессий

### Этап 1 — Библиотека и запуск игр (MVP)
- Парсинг `appmanifest_*.acf` → установленные игры (ValveKeyValue)
- `GetOwnedGames` → полный список владения, кэш в `Games`
- Merge owned ∪ installed → единая модель для UI
- Обложки — напрямую из `appcache/librarycache/`
- UI: сетка карточек, поиск по названию
- Запуск: `steam://rungameid/<appid>`
- `FileSystemWatcher` на `steamapps/` → живое обновление статуса без ручного рефреша

### Этап 2 — Установка / обновление
- Кнопка на карточке переключается Install/Update в зависимости от состояния
- Оба случая — один и тот же вызов `steam://install/<appid>`
- Чтение `StateFlags` (бит `2` = update required) → бейдж "обновление доступно"
- Пересканирование `.acf` по получению фокуса окном, не чаще раза в минуту

### Этап 3 — Жанры и теги
- `appinfo.vdf` (бинарный) через ValveKeyValue → `genres`, `store_tags` (id-списки) по `app_id`
- `localization.vdf` → словарь `id → имя`, кэш в `Genres`/`Tags`
- Fallback на недостающие id: `store.steampowered.com/tagdata/populartags/<lang>`
- UI: фильтры и сортировка по жанру/тегу

### Этап 4 — Друзья и коллекции
- `GetFriendList` + `GetOwnedGames` по каждому другу → кэш в `Friends`/`FriendGames`
- На карточке игры — аватарки друзей-владельцев (с учётом того, что часть друзей будет отфильтрована приватностью — см. предыдущее обсуждение)
- Свои коллекции (независимые от формата Steam) — CRUD, сортировка, привязка игр к коллекциям
- UI для ручного управления тегами/папками поверх собственных данных

## 5. Что держать в голове с самого начала

- **Graceful degradation**: нет Steam-клиента, нет библиотек, приватный профиль — приложение не должно падать, а показывать пустое состояние
- **Разная частота обновления кэша**: локальные файлы — дёшево, можно часто; Web API (owned games, friends) — редко, по таймеру или вручную
- **Юнит-тесты на VDF/ACF-парсеры с первого дня**: заведите тестовые фикстуры (сэмплы реальных файлов) — формат между версиями клиента иногда слегка меняется, тесты ловят регрессии раньше, чем ручная отладка
- **Логирование** (хотя бы в файл) — пригодится именно для диагностики парсинга VDF на машинах пользователей, если проект пойдёт дальше личного использования

## 6. Работа через Claude Code

- Этот файл — как `CLAUDE.md`/`PROJECT_PLAN.md` в корне репозитория, чтобы контекст подхватывался автоматически
- Каждый этап (0–4) — отдельная сессия/ветка с явным чек-листом задач
- Начинать стоит строго по порядку: Этап 0 → рабочий коммит → Этап 1, не забегая вперёд на Этап 3/4 до готовности merge-логики владения/установки

## 7. Не блокирует старт, но стоит решить в процессе

- Имя проекта/репозитория
- GitHub с первого коммита или сначала локально
- Публикация как open-source — тогда сверить лицензии зависимостей (WPF-UI — MIT, ValveKeyValue — открытая; на момент публикации стоит перепроверить актуальные условия)
