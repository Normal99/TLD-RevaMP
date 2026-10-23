# TV Browser

A web browser on the game's TV, used with the keyboard. In a session everyone sees the same page, and TLD RevaMP's
replays show what was on the TV. An example of the multiplayer mod API's shared records and events, and of replay
channels (API 4).

## Using it

Switch the TV on (its own button), look at it from within 4 m and press **U**. The keyboard now goes to the page
until **Esc**:

| Keys | What they do |
| --- | --- |
| typing, Backspace, Delete, Tab, Enter, the arrows, Home, End | as in any browser, into the page |
| Page Up / Page Down, Space, the mouse wheel | scroll |
| **F2**, then the letters on a label | link hints: click a link or button, or go into a field, without a mouse |
| Ctrl+L or F6 | the address bar: an address, or words to search for (DuckDuckGo's HTML search by default) |
| Alt+← / Alt+→ | back / forward |
| F5 or Ctrl+R | reload |
| Ctrl+H | the TV's start page |
| Ctrl+V | paste |

The first time, the TV asks before downloading the browser (Chromium's headless shell, about 120 MB, from Google's
Chrome for Testing storage, checked against a pinned SHA-256). It is kept in `BepInEx/config/TvBrowser/`.

Settings (`BepInEx/config/tldrevamp.tvbrowser.cfg`): the key, how close you must be, how far away a TV keeps its page
open (25 m), the picture rate (15 a second at most) and quality, the search address, and whether the browser runs as
a single process under Proton (on by default: about 0.7 GB instead of 1.4 GB).

## How it is synced

Nothing is streamed. Each machine runs its own browser, and the TV's page is the same on every machine because the
same things go into it:

- **The shared state** (`TvState` in `Core.cs`, one record per TV, keyed by the TV's world id like the head unit's):
  the address, a navigation counter, which player has the keyboard (the driver), and the scroll position.
- **Keys** go to everyone as `keys` events (batched every 0.1 s). Every machine puts the same keys into the same page
  at the same fixed size (1024x576), so typing, Tab, Enter and the F2 hints land on the same things. Hints run in
  the page and take their keys from it, so they need no extra sync.
- **The driver's browser decides**: when its page goes somewhere (a link, a form, a redirect) it shares the address,
  and others follow; it shares its scroll position up to 4 times a second, and others move when they are more than
  24 px away. One player has the keyboard at a time; another can take it.

Each machine checks addresses itself: only `http` and `https`, no user names in them, and no local or private hosts
(localhost, 127.x, 10.x, 192.168.x and the like). Its browser also refuses those hosts itself, for links and
redirects inside pages too. Downloads, pop-ups (opened in the same TV instead) and JavaScript dialogs are turned off.

Only addresses, keys and scroll positions pass between players. A page's sign-ins stay on each machine: the profile
is a temporary folder, emptied when the game closes.

## Replays

`ReplayTv.cs` records each TV near the player as a replay channel: a half-size, low-quality picture of the page
whenever it changed (at most every 2 s), and "off" when it goes off. Playback puts the picture at the playhead on the
TV's copy. Replays are local files, but they do hold those pictures of whatever the pages showed.

## Files

| File | What it does |
| --- | --- |
| `Plugin.cs` | Settings, API registration, the per-frame runner, a `TvScreen` on every display |
| `TvScreen.cs` | On every display: its shared state, its page, showing the picture, following the driver |
| `Keyboard.cs` | Taking the keyboard, keys into commands, the address bar, sending keys to others, the on-screen help |
| `Core.cs` | The shared state and its edits, addresses and their checks, keys, commands (pure, unit tested) |
| `Browser.cs` | Downloading, starting and stopping the browser; its pages; pop-ups |
| `Cdp.cs`, `Json.cs` | The DevTools protocol over a WebSocket of its own (no libraries) |
| `Tab.cs`, `PageInput.cs` | One TV's page: picture, addresses, keys and the wheel as DevTools input |
| `Pages.cs` | The F2 link-hints script and the start page |
| `ReplayTv.cs` | The replay channel |
| `Probe.cs` | Checks for `tools/tvbrowser.py` through the debug bridge |

## Tests

```sh
dotnet run --project tools/unit/TvBrowserCoreTest.csproj                            # state, edits, addresses, keys
TVB_CHROME=/path/to/chrome-headless-shell dotnet run --project tools/unit/TvBrowserPageTest.csproj   # in a real browser
tools/tvbrowser.py                                                                  # two players in the game
```

## Licence

GPL-3.0, like TLD RevaMP. Chromium's headless shell is BSD-licensed and is downloaded, not shipped.
