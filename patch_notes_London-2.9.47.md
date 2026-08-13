# Multiworld Launcher 2.9.47

## Licensing

This release documents every open-source component the launcher and the Diablo
II mod are built from, and ships their licences alongside the binaries.

- **`THIRD-PARTY-NOTICES.md`** lists each component: what it does, which files
  it provides, who wrote it, its licence and where its source lives. The full
  text of every licence is in `licenses/`, readable in the repository without
  downloading anything.
- **`LICENSE`** is the Apache License 2.0 and covers this project's own code.
  **`NOTICE`** states that scope explicitly: the components distributed
  alongside it — d2gl (GPL-3.0), SGD2FreeRes (AGPL-3.0-or-later), DSOAL
  (LGPL-2.1), D2.Detours, D2MOO and cnc-ddraw (MIT), SFmpqapi (BSD) — remain
  under their own licences, and nothing here grants any right over them.
- These files now travel with the build, so the licences accompany the
  binaries wherever the package goes, as the GPL, AGPL and LGPL require.

The launcher's own library links only against Windows system libraries; the
components above are independent programs that Diablo II loads in their own
right.
