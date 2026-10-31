# PvP Chronicle

[English](README.md) | [简体中文](README.zh-CN.md)

PvP Chronicle is a Dalamud plugin for recording and reviewing FINAL FANTASY XIV
PvP matches. It supports Frontline and Crystalline Conflict history, replay,
map objectives, performance statistics, and evidence-bounded behavior review.

## Install

1. Install and start XIVLauncher with Dalamud enabled.
2. Open **Dalamud Settings → Experimental**.
3. Add this custom repository URL:

   ```text
   https://raw.githubusercontent.com/chefiffer/pvpchronicle/main/repo.json
   ```

4. Open the Plugin Installer, search for **Battlefield Frontline Collector**,
   and select **Install**.

## Highlights

- Local Frontline and Crystalline Conflict match history and detailed replay.
- Score, KDA, combat-flow, death, movement, marker, and observable objective
  timelines.
- PvP calendar, map rotation, map-specific mechanics, and localized UI.
- Optional upload for server-generated Logs and reference distributions.

PvP Chronicle does not automate gameplay, execute combat actions, reveal hidden
game state, or treat incomplete observations as definitive cheating evidence.

## Contributing

Fork the repository, create a focused branch, add tests for the affected
behavior, and open a pull request against `main`.

Do not submit match captures, player datasets, access tokens, private server
addresses, or other users' personal data. Report security issues according to
[`SECURITY.md`](SECURITY.md).

## License

The PvP Chronicle client source in this repository is licensed under the
[GNU Affero General Public License v3.0](LICENSE).

That license applies only to original PvP Chronicle client source and other
files expressly identified as AGPL-3.0-only. It does not relicense FINAL
FANTASY XIV materials, third-party libraries, or fonts. Those components
remain subject to their respective owners and license terms.

FINAL FANTASY XIV names, imagery, and icons remain the property of Square Enix.
See [`plugin/THIRD-PARTY-NOTICES.md`](plugin/THIRD-PARTY-NOTICES.md) for game
material attribution and third-party notices.

PvP Chronicle is an independent, non-commercial community project and is not
affiliated with or endorsed by Square Enix.
