# WF Revival Patcher changelog

Called Warpforge Patcher up to 0.4.0.

The Android app that patches the game on the device itself. Write "device", not "phone", unless something only applies to phones. Newest first. A heading ending in
"(test build)" is published as a pre-release.

## 0.4.2 - 2026-10-28

### New
- **No more black screen while the game starts.** A game patched with this version shows "WF Revival" and "Loading…" from the moment you open it until the game's own loading screen appears. Press **Patch and install** once to get it; your settings, mod and signing key are kept.

### Fixed
- Having a newer mod on the device than the server's (a test build, for example) no longer stops patching. The newer mod is kept, and **Update the mod only** now says so instead of showing an error.

### Changes
- Patching from a PC with the scripts and a cable is archived and no longer updated. This app is the way to patch the game.

## 0.4.1 - 2026-10-21

**On 0.4.0 or older? Install this version by hand once** (download the APK below and open it; your saved signing key and settings are kept). The project's GitHub page moved to `silentanarchist/wf-revival-mod`, and older versions of the app can't update themselves from the new address. From 0.4.1 on, updates arrive in the app again.

### Changes
- **The app is now called WF Revival Patcher.** The project dropped "Warpforge" from its own names. It's the same app: it installs over the old one and keeps everything.
- Looks for its updates at the project's new GitHub address.
- Ready for the renamed mod file (`WFRevival.Android.dll`, coming with a later mod update): it checks the signature of mod builds under either name and puts each one in place under its own name, so the game never loads two copies.

## 0.4.0 - 2026-10-21

### New
- **The app speaks your language.** Every screen, button and message is available in all 13 languages the game ships: English, Spanish, Russian, French, German, Portuguese (Brazil), Italian, Korean, Simplified Chinese, Japanese, Polish, Czech and Turkish. The app follows your device's language; the language button in the top right corner lets you pick another one.
- Translations are first drafts; corrections reach the app from the server without an app update.

### Changes
- **Check for updates** and the language button are bigger, and the log at the bottom is a little taller.
- After **Update the mod only**, the black screen fix or loading a signing key, a short message confirms it worked.

## 0.3.0 - 2026-10-17

Required update

### New
- **Supports signed mod releases** (mod 0.14.0 and newer). Every mod build the app downloads must carry the Warpforge Revival project's signature.

### Security
- A build that's unsigned, signed with the wrong key or modified after signing is refused, and so is one older than the mod already on your device. This is the same check the mod's own updater uses from 0.14.0.

## 0.2.8 - 2026-10-17

### Changes
- The app now looks for its updates in the merged Warpforge Revival repository (`silentanarchist/warpforge-revival-mod`), where new versions of the app are released from now on. Update to this version to keep getting updates.

## 0.2.7 - 2026-10-14

### Changes
- The community server is now `wf-revival.silentanarchist.com`, matching the mod. Settings that still name `warpforge.silentanarchist.com` keep working; it's the same server.

## 0.2.6 - 2026-10-14

### New
- **Server setting on the Tools page.** Shows which server the mod connects to and lets you change it, or switch back to the community server, without editing files. Close the game fully first; the change applies the next time it starts.

### Changes
- The app's text says "device" instead of "phone".

## 0.2.5 - 2026-10-14

### Fixed
- Tapping outside Android's install, removal or update confirmation could leave the app stuck. On devices where that happens, the button now reads "Continue: …" and brings the confirmation back.

### New
- **Tools page.** A menu under the title switches between **Patcher** and **Tools**. Tools holds:
  - **The black screen fix.** Apply or remove it on an already patched game, without patching again.
  - **Save card art export to Downloads.**
  - **The signing key buttons.**

### Changes
- The log area at the bottom is half as tall.

## 0.2.4 - 2026-10-13

### New
- **Save card art export to Downloads.** After exporting card art in the game (Settings > Mod > Export card art), this button copies the export to your phone's Download folder so it's easy to find. The app shows when the export was made and its size.

## 0.2.3 - 2026-10-13

### New
- **The app updates itself.** When a newer Warpforge Patcher is released, a pop-up offers it when you open the app: **Update now** or **Later**. Updating downloads it, checks it and installs it over the old version. Your saved signing key is kept, so nothing needs redoing.
- **Check for updates** button in the top right corner, to look for a new version at any time.
- If you choose **Later**, an "Update this app" button stays under the status until you update.
- Updates are only installed when they come from this project's GitHub releases, match the checksum in their release notes, and are signed with the same key as the app you have.

Install this version once by hand. From now on, updates arrive through the app.

## 0.2.1 - 2026-10-11

### Changes
- The "Export the game's downloaded files" button is gone. The revival server hands those files out now, so they no longer need backing up.
- Before it removes the installed game, the app asks a plain "Remove the game first?" (Continue or Stop) and no longer asks about a backup.

Tested on a phone.

## 0.2.0 - 2026-10-11

### New
- First release. Patches your own copy of the game (version 1.35.0) on the phone: from the
  installed game or from a `.apks` / `.xapk` backup. Then it installs the patched game and puts the
  current mod in place. Same patching as the PC's `1 - patch.bat`.
- Warns before it has to remove the installed game, because that erases the game's downloaded files
  for good, and goes on only once you confirm you have a backup.
- **Export the game's downloaded files**, on Android 12 and older (Android 13 and newer do not let any
  app read them; use a PC there).
- **Fix black menu after loading**, ticked by default on Pixel 6 and later and on Samsungs with an
  Exynos chip and Mali graphics: shows a still picture instead of the opening video.
- **Update the mod only**, **Share logs** (never includes your game login), and saving or loading the
  app's signing key.

Tested in MuMu Player and on a Pixel 6 Pro.
