# FullParty

FullParty is a Dalamud plugin by gerulla.

## Prerequisites

* XIVLauncher, FINAL FANTASY XIV, and Dalamud are installed.
* The game has been launched with Dalamud at least once.
* A compatible .NET SDK is installed.
* NuGet can resolve `Dalamud.NET.Sdk`.

## Building

Build the plugin from the repository root:

```powershell
dotnet build FullParty.sln -c Debug
```

The debug plugin DLL is written to:

```text
FullParty/bin/x64/Debug/FullParty.dll
```

## Loading In Game

1. Launch the game through XIVLauncher.
2. Open Dalamud settings with `/xlsettings`.
3. Go to `Experimental` and add the full path to `FullParty.dll` under Dev Plugin Locations.
4. Open the plugin installer with `/xlplugins`.
5. Go to `Dev Tools` > `Installed Dev Plugins`.
6. Enable `FullParty`.
7. Open the main window with `/fullparty`.

## Publishing

Releases are automated through `.github/workflows/release.yml`.

Before publishing, configure these GitHub repository variables:

* `FULLPARTY_CLIENT_ID` - required production OAuth device client ID. This can also be a repository secret.
* `FULLPARTY_BASE_URL` - optional, defaults to `https://fullparty.gg`.
* `FULLPARTY_DEBUG` - optional, defaults to `False`.

To publish a release, push a SemVer tag:

```powershell
git tag v0.0.1
git push origin v0.0.1
```

The workflow builds the plugin, uploads `FullParty.zip` to the GitHub release, and updates `repo.json` on the default branch. Users can install from the custom repository URL:

```text
https://raw.githubusercontent.com/gerulla/fullparty-plugin/master/repo.json
```

## References

* [Dalamud Developer Docs](https://dalamud.dev)
* [Plugin submission docs](https://dalamud.dev/plugin-publishing/submission)
