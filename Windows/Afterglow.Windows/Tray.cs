using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Afterglow.Windows;

internal sealed class TrayIcon
{
    private const int CallbackMessage = 0x8000 + 24;
    private Window? window;
    private HwndSource? source;
    private nint icon;
    private Action? pause;
    private bool added;

    public void Attach(Window owner, Action togglePause)
    {
        window = owner;
        pause = togglePause;
        source = (HwndSource)PresentationSource.FromVisual(owner);
        source.AddHook(Hook);
        icon = LoadIcon();
        added = Notify(0, Tip: "AFTERGLOW");
    }

    public void Update(string tip)
    {
        if (!added) return;
        if (tip.Length > 127) tip = tip[..127];
        Notify(1, tip);
    }

    public void Remove()
    {
        if (added) Notify(2, "");
        added = false;
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
        source?.RemoveHook(Hook);
        source = null;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage || window is null) return 0;
        var mouse = (int)(lParam.ToInt64() & 0xFFFF);
        if (mouse is 0x0202 or 0x0203) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); handled = true; }
        else if (mouse == 0x0205) { ShowMenu(); handled = true; }
        return 0;
    }

    private void ShowMenu()
    {
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(hwnd);
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, Background = Theme.Panel, Foreground = Theme.Foreground, BorderBrush = Theme.Line };
        void Item(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Open", () => { window.Show(); window.Activate(); });
        Item("Pause", () => pause?.Invoke());
        Item("Quit", () => window.Close());
        menu.IsOpen = true;
    }

    private bool Notify(uint command, string Tip)
    {
        if (source is null) return false;
        var data = new NotifyData
        {
            Window = source.Handle,
            Id = 1,
            Flags = 7,
            Callback = CallbackMessage,
            Icon = icon,
            Tip = Tip
        };
        data.Size = Marshal.SizeOf<NotifyData>();
        return Shell_NotifyIcon(command, ref data);
    }

    private static nint LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return 0;
        var buffer = new char[260];
        var count = Math.Min(path.Length, buffer.Length - 1);
        path.CopyTo(0, buffer, 0, count);
        var index = 0;
        return ExtractAssociatedIcon(0, buffer, ref index);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int Size;
        private int pad0;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        private uint pad1;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint ExtractAssociatedIcon(nint instance, char[] path, ref int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
