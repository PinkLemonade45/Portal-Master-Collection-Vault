# v0.1.0 - First Release

First public release build of Portal Master Vault.

## Included

- Windows desktop app with WebView2 UI.
- Local catalog support for Spyro's Adventure, Giants, SWAP Force, Trap Team, SuperChargers, and Imaginators.
- Portal scanning with UID-aware collection entries.
- SWAP Force top/bottom scan handling and combined scan reveal presentation.
- Local portraits, element icons, backgrounds, summon sounds, and scan animations.
- Advanced include/exclude filters and grouping.
- Discord Rich Presence support.
- Release-mode cleanup: no scan injection tools, no figure dump controls, no portal debug strip, no upgrade editor tab, no no-UID cleanup button, no devtools/context menu/status bar, and no Release PDB output.

## Known Notes

- Villain/trap display code is present but villains are hidden for now.
- Figure stats reading is disabled for now while level/stat decoding is being verified.
- Catalog and asset coverage may still need corrections as more physical figures are tested.
- The app expects the bundled `app`, `assets`, `data`, and `runtimes` folders to remain beside the executable.
