# Before publishing a new version

Copy this list into the release draft (`docs/releases/vX.Y.Z.md`) and the pull request. Each item exists because a
release went wrong or nearly did without it.

- [ ] Version: `ModVersion` and `Mod.Version` match; one `<ChangeLog>` entry for the version; "New in" lines on the
      mod page updated.
- [ ] Test results: the mod page's "Tested results" come from the latest A/B runs on this version (paired game hours
      from `captures.csv`), with caveats stated. Never publish with the previous version's numbers.
- [ ] Screenshots: current UI only (no panel text that has since been fixed; crop if needed), JPG under 2.1 MB,
      **10 screenshots at most** in `PublishConfiguration.xml` (drop older ones to make room), names .jpg/.jpeg/.png
      of 128 characters or less. Full-size originals: `%USERPROFILE%\OneDrive\Pictures\Screenshots 1`.
- [ ] Paradox account: the account linked to the Steam account on this PC owns the mod and its **email is verified**
      (Paradox account settings). The publisher signs in through Steam, not through the game's sign-in.
- [ ] `tools\check.ps1` passes; everything committed on `main` (release branch merged through a pull request).
- [ ] Game closed. Then `tools\publish.ps1` (uploads, tags vX.Y.Z, pushes the tag). The mod shows "pending
      publication" for a few minutes.
- [ ] Push `main` from your own terminal; check the live mod page (version, text, screenshots). Text and images can be
      fixed without a new version: `tools\publish.ps1 -PublishProfile UpdatePublishedConfiguration`.
