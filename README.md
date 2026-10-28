# WF Revival mod

*Formerly "Warpforge Revival". Since 0.15.0 the mod files are `WFRevival.dll` / `WFRevival.Android.dll`
(`WarpforgeRevival.dll` / `WarpforgeRevival.Android.dll` before); an older copy updates and renames itself.*

A [MelonLoader](https://melonwiki.xyz/) mod that lets *Warhammer 40,000: Warpforge* run against a
community-hosted server now that the game's own servers are closed.

This repository is the complete source of both builds of the mod: `WFRevival.dll` for
Windows and `WFRevival.Android.dll` for Android (the same source; the parts marked
`ANDROID_PORT` are the Android differences). It also holds the PC patcher that puts the mod loader
into the Android game, and the changes made to that loader. **Installing? See the
[guides on the website](https://wf-revival.silentanarchist.com/guides).** (Until 2026-10-17 the Android parts were in their own repository,
[wf-revival-mod-android](https://github.com/silentanarchist/wf-revival-mod-android). It
stays up so older copies of the Warpforge Patcher app can find their update; the app is released here now.)
Versions are numbered `x.y.z.f-w` for Windows and `x.y.z.f-a` for Android: `x.y.z` is the shared code
and always the same on both; `f` counts fixes made for one of them only, is left off while it is 0
(`0.12.1-w`), and starts again with every new `x.y.z`. The server it talks to is in
[wf-revival-server](https://github.com/silentanarchist/wf-revival-server).

This is an unofficial fan project. It is not affiliated with or endorsed by Everguild or
Games Workshop. It contains no game files: you need your own copy of the game.

**About AI use.** The code and documentation in this repository were written largely by an AI
assistant (Anthropic's Claude), working under the maintainer's direction; the maintainer decided
what to build and tested it in the game. Testing is by hand and limited to what the maintainer
has played, so expect bugs. Read the code before you rely on it, and please report what you find.

## What it does

- **Sends the game's server requests to a revival server** instead of the closed ones (`PlayFabTransport.cs`).
- **Matches** run on the revival server's own match service. The game never contacts Photon's
  servers (`PhotonService.cs`, `Matchmaking.cs`). The mod proves to the match service who is
  connecting with a match ticket from the game server (`MatchTicket.cs`), and carries match
  connections inside https when the server offers it (`MatchTunnel.cs`).
- **Global chat** runs through the revival server (`GlobalChat.cs`). **Report message** on a player
  goes to the server, where its admins see it; **Block player** is the game's own and hides that
  player's messages for you only.
- **AI opponent emotes** now and then instead of after every move (`BotEmotes.cs`).
- **Custom Test**, a 60-card mode added by the revival (`LongGame.cs`).
- **Profile additions:** win/loss record and skulls, friend codes, free renames (`ProfileStats.cs`, `ProfilePage.cs`).
- **Accounts:** when a server requires one, a sign-in window appears while the game loads (or a
  game login file from the server's website is used); otherwise linking a site account is
  optional (`GameSignIn.cs`, `AccountPage.cs`). With an account, **Sign out** in Settings >
  Account signs out this computer only; the account keeps its game progress.
- **Encrypted connection:** a server address without `http://` is tried over https first, and a
  server that has answered over https is never contacted unencrypted again (`RevivalConfig.cs`, `Net.cs`).
- **Updates itself** from the server it is connected to: when the server has a newer build, the
  sign-in window says so while the game loads, downloads it, and the game closes and starts
  again with it (`Updater.cs`). `AutoUpdate = Off` in the settings file switches this off.
- **Stops the game contacting Unity's online services** (a sign-in that fails since the shutdown,
  and usage reports to the publisher); the two voice-line switches those services carried are set
  by the mod (`UnityServicesOff.cs`).
- **Mode rules shown where you build decks:** warlord health for the mode being built, and the
  practice Classic/Skirmish switch choosing the AI's deck and health (`LongGame.cs`, `PracticeMenu.cs`).
- **Mod version** next to the game's in Settings > General (`VersionLabel.cs`).
- Smaller fixes: the game keeps running in the background, scrollbars on long lists, missing text
  supplied by the server, menus for closed features hidden.

Each file starts with a comment saying what it is for and why.

## Download

Every version is on the [Releases](https://github.com/silentanarchist/wf-revival-mod/releases) page: since
0.14.0 one release, "WF Revival <version>" (before 2026-10-21 "Warpforge Revival <version>"), holds both the Windows and the Android build.

The latest builds are [releases/WFRevival-0.15.0-w.zip](releases/WFRevival-0.15.0-w.zip) (Windows) and
[releases/WFRevival.Android-0.15.0-a.zip](releases/WFRevival.Android-0.15.0-a.zip) (Android; see
[docs/android.md](docs/android.md)); open one and press "Download raw file". They are built from the source in this
repository and signed by the project owner (the mod installs only signed updates). The Windows zip holds the mod, the
`manifest.json` it needs, and a short README.

## Installing and making an account

The step-by-step guides, with screenshots, are on the Revival website, the one place they are kept up to
date. **The website needs an account:** sign in (or make a free account) at
[wf-revival.silentanarchist.com](https://wf-revival.silentanarchist.com/login) to read them.

- [PC (Windows)](https://wf-revival.silentanarchist.com/guides/pc)
- [Android device: the Warpforge Patcher app](https://wf-revival.silentanarchist.com/guides/phone) (the easiest way on Android)
- Android device with a PC and a cable: **archived** (the scripts are kept in `archive/pc-patcher/`, no longer updated); use the app above.
- [Android on a PC with MuMu Player](https://wf-revival.silentanarchist.com/guides/mumu)

No account yet? Make one on the [website](https://wf-revival.silentanarchist.com/login): you can read
the guides straight away, even while the account waits for approval. You can also make one in the game's own
sign-in window, which appears while the game loads. The same account works on PC and Android.

## Building

You need the .NET SDK (6 or newer) and three folders of reference libraries from your own
installation. They are not in this repository because they are not ours to publish.

| Folder | Where it comes from |
|---|---|
| `refs/net6` | the .NET 6 runtime libraries (`Microsoft.NETCore.App` 6.x) |
| `refs/ml` | `MelonLoader\net6` in your game folder |
| `refs/il2cpp` | `MelonLoader\Il2CppAssemblies` in your game folder (created on the game's first run with MelonLoader) |

```
dotnet build WarpforgeRevival/WarpforgeRevival.csproj -c Release -p:RefsDir=<path to refs>
```

The result is `WarpforgeRevival/bin/Release/net6.0/WFRevival.dll` (the source folders keep their old names).

The Android build (`WarpforgeRevival.Android/WarpforgeRevival.Android.csproj`) needs the loader's own
reference libraries; see [docs/android.md](docs/android.md) under "Building".

Built for game version 1.35.0. The mod hooks specific game functions, so another game version
would need those checked again.

## Licence

MIT - see [LICENSE](LICENSE). The licence covers this project's own work only, not the game.
The Android patcher and mod loader are other people's software, changed by this project, under
their own licences: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
