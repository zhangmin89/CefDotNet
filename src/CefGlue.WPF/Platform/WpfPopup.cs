using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xilium.CefGlue.Common.Platform;

namespace Xilium.CefGlue.WPF.Platform
{
    /// <summary>
    /// The WPF popup wrapper.
    /// </summary>
    internal class WpfPopup : WpfOffScreenControlHost, IOffScreenPopupHost
    {
        private readonly object _lifecycleLock = new object();
        private bool _disposed;

        public WpfPopup(Popup popup) : base(popup)
        {
        }

        private Popup Popup => (Popup) base._control;

        protected override IInputElement MousePositionReferential => Popup.PlacementTarget;

        public int Width => (int)Popup.Width;

        public int Height => (int)Popup.Height;

        public int OffsetX => (int)Popup.HorizontalOffset;

        public int OffsetY => (int)Popup.VerticalOffset;

        public void MoveAndResize(int x, int y, int width, int height)
        {
            if (IsDisposed)
            {
                return;
            }

            Popup.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal, 
                new Action(() =>
                {
                    lock (_lifecycleLock)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        Popup.HorizontalOffset = x;
                        Popup.VerticalOffset = y;
                        Popup.Width = width;
                        Popup.Height = height;
                    }
                }));
        }

        public void Open()
        {
            SetIsOpen(true);
        }

        public void Close()
        {
            SetIsOpen(false);
        }

        private void SetIsOpen(bool isOpen)
        {
            if (IsDisposed)
            {
                return;
            }

            Popup.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    lock (_lifecycleLock)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        Popup.IsOpen = isOpen;
                    }
                }));
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Popup.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Popup.IsOpen = false));
        }

        private bool IsDisposed
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _disposed;
                }
            }
        }

        protected override void SetContent(Image image)
        {
            Popup.Child = image;
        }
    }
}
