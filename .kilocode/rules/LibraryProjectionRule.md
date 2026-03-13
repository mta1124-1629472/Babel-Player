# Library Projection

Library browsing is projection-based.

## Guidelines

- WinUI may construct TreeView nodes for library display.
- WinUI may handle node expansion and drag/drop visuals.
- Queue mutations and playback actions must not originate in the UI layer.
