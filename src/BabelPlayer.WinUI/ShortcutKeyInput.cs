using Windows.System;

namespace BabelPlayer.WinUI;

public readonly record struct ShortcutKeyInput(VirtualKey Key, bool Ctrl, bool Alt, bool Shift);
