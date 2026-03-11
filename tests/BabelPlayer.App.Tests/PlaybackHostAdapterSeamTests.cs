using BabelPlayer.App;
using BabelPlayer.Core;
using BabelPlayer.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BabelPlayer.App.Tests;

#pragma warning disable CS0067
#pragma warning disable SYSLIB0050

public sealed class PlaybackHostAdapterSeamTests
{
    [Fact]
    public void PlaybackHostAdapter_ComposesMpvBackendWithFakePresenter()
    {
        var backend = new MpvPlaybackBackend();
        var presenter = new FakeVideoPresenter();
        var adapter = new PlaybackHostAdapter(backend, presenter);

        Assert.Same(presenter.View, adapter.View);
        Assert.Equal(HardwareDecodingMode.AutoSafe, adapter.HardwareDecodingMode);

        adapter.RequestHostBoundsSync();
        Assert.Equal(1, presenter.RequestBoundsSyncCount);

        using (adapter.SuppressNativeHost())
        {
            Assert.Equal(1, presenter.SuppressCount);
        }

        Assert.Equal(0, presenter.SuppressCount);
        Assert.Equal(new RectInt32(10, 20, 300, 200), adapter.GetStageBounds(presenter.View));
    }

    private sealed class FakeVideoPresenter : IVideoPresenter
    {
        private readonly FrameworkElement _view = (FrameworkElement)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Border));
        private readonly RectInt32 _stageBounds = new(10, 20, 300, 200);

        public event Action? InputActivity;
        public event Action? FullscreenExitRequested;
        public event Func<ShortcutKeyInput, bool>? ShortcutKeyPressed;

        public FrameworkElement View => _view;

        public int RequestBoundsSyncCount { get; private set; }

        public int SuppressCount { get; private set; }

        public void Initialize(Window ownerWindow, IPlaybackBackend playbackBackend)
        {
        }

        public void RequestBoundsSync()
        {
            RequestBoundsSyncCount++;
        }

        public IDisposable SuppressPresentation()
        {
            SuppressCount++;
            return new ReleaseScope(this);
        }

        public RectInt32 GetStageBounds(FrameworkElement relativeTo)
        {
            return _stageBounds;
        }

        private sealed class ReleaseScope : IDisposable
        {
            private FakeVideoPresenter? _owner;

            public ReleaseScope(FakeVideoPresenter owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                if (_owner is null)
                {
                    return;
                }

                _owner.SuppressCount--;
                _owner = null;
            }
        }
    }
}

#pragma warning restore SYSLIB0050
#pragma warning restore CS0067
