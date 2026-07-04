# FullParty

**Dalamud Plugin Companion for FullParty.gg**

FullParty brings your FullParty.gg raid planning directly into Final Fantasy XIV. Link your FullParty account, browse your moderator groups, open upcoming runs, inspect rosters, and review applications without tabbing back to the website.

---

## Installation

Add this custom plugin repository in Dalamud:

```text
https://raw.githubusercontent.com/gerulla/fullparty-plugin/master/repo.json
```

Then open `/xlplugins`, search for `FullParty`, install it, and open the main window with:

```text
/fullparty
```

FullParty requires a FullParty.gg account. The plugin will ask you to approve access through the website using the OAuth device login flow.

---

## Features

FullParty is focused on helping group moderators manage scheduled content from inside the game.

### Current Features

* Link your FullParty.gg account from inside the plugin.
* View your FullParty user and primary character.
* View characters associated with your account.
* Browse groups where you have moderator access or higher.
* View upcoming runs for your selected group.
* Automatically surface moderator runs starting within 60 minutes.
* Automatically open an upcoming run window when entering Occult Crescent.
* View run rosters split by party, with bench separated below the main roster.
* See character, class, and phantom job icons in roster slots.
* Open assigned users' applications directly from roster slots.
* Review application details, preferred jobs, phantom jobs, raid positions, progress, and user history.
* Connect to a run's live room and see connected characters.

### Moderator Tools

The run window includes early controls for:

* Ready Check Alliance
* Run Check-In
* Live Room connection

Live command execution is still being built out. The current live room support focuses on presence and connected character visibility.

---

## Commands

```text
/fullparty
```

Opens the main FullParty window.

```text
/fullparty debug
```

Opens the debug/settings window with environment and auth state details.

---

## Perfect For

* FullParty.gg group moderators.
* Forked Tower and Occult Crescent organizers.
* Raid leads who want roster and application context in-game.
* Groups that coordinate scheduled runs through FullParty.gg.

---

## Custom Repository

This plugin is distributed through a GitHub-backed Dalamud custom repository. The repository manifest is:

```text
https://raw.githubusercontent.com/gerulla/fullparty-plugin/master/repo.json
```

Release zips are attached to GitHub releases and referenced by `repo.json`.

---

## Development

### Prerequisites

* XIVLauncher, FINAL FANTASY XIV, and Dalamud are installed.
* The game has been launched with Dalamud at least once.
* A compatible .NET SDK is installed.
* NuGet can resolve `Dalamud.NET.Sdk`.

### Local Environment

Create a local `.env` file in the repository root:

```text
FULLPARTY_BASE_URL=https://fullparty.gg
FULLPARTY_CLIENT_ID=the-generated-oauth-client-uuid
DEBUG=True
```

For local website development, use:

```text
FULLPARTY_BASE_URL=http://fullparty.test
```

### Building

Build the plugin from the repository root:

```powershell
dotnet build FullParty.sln -c Debug
```

The debug plugin DLL is written to:

```text
FullParty/bin/x64/Debug/FullParty.dll
```

### Loading As A Dev Plugin

1. Launch the game through XIVLauncher.
2. Open Dalamud settings with `/xlsettings`.
3. Go to `Experimental`.
4. Add the full path to `FullParty.dll` under Dev Plugin Locations.
5. Open the plugin installer with `/xlplugins`.
6. Go to `Dev Tools` > `Installed Dev Plugins`.
7. Enable `FullParty`.

---

## Publishing

Releases are automated through `.github/workflows/release.yml`.

Before publishing, configure these GitHub repository variables:

* `FULLPARTY_CLIENT_ID` - required production OAuth device client ID. This can also be a repository secret.
* `FULLPARTY_BASE_URL` - optional, defaults to `https://fullparty.gg`.
* `FULLPARTY_DEBUG` - optional, defaults to `False`.

To publish a release, push a SemVer tag:

```powershell
git tag v0.0.1
git push origin master
git push origin v0.0.1
```

The workflow builds the plugin, uploads `FullParty.zip` to the GitHub release, and updates `repo.json` on the default branch.

---

## References

* [FullParty.gg](https://fullparty.gg)
* [Dalamud Developer Docs](https://dalamud.dev)
* [Dalamud Plugin Submission Docs](https://dalamud.dev/plugin-publishing/submission)
