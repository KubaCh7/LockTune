using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LockTune.Platform;

/// A global shortcut (modifiers + virtual key), stored in the settings.
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static readonly Hotkey Default = new(Native.MOD_CONTROL | Native.MOD_ALT, 0x4C); // Ctrl+Alt+L

    public override string ToString()
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}

/// Registers one system-wide hotkey through a message-only window.
public sealed class HotkeyManager : IDisposable
{
    private const int Id = 0x4C54; // "LT"
    private readonly HwndSource source;
    private bool registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("LockTuneHotkeys") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        source = new HwndSource(parameters);
        source.AddHook(WndProc);
    }

    /// Returns false when another app already owns the shortcut.
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        registered = Native.RegisterHotKey(source.Handle, Id, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.VirtualKey);
        return registered;
    }

    public void Unregister()
    {
        if (registered) Native.UnregisterHotKey(source.Handle, Id);
        registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        source.Dispose();
    }
}

/// Fires once when the user has been idle for `Threshold`.
public sealed class IdleMonitor
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool fired;

    public bool IsEnabled { get; set; }
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMinutes(5);
    public event Action? Idle;

    public IdleMonitor()
    {
        timer.Tick += (_, _) =>
        {
            var idle = Native.IdleTime();
            if (idle < Threshold) { fired = false; return; }
            if (fired || !IsEnabled) return;
            fired = true;
            Idle?.Invoke();
        };
    }

    public void Start() => timer.Start();
}

/// Keeps the display on (until a deadline, or indefinitely).
public sealed class DisplayAwake
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime? until;
    public bool IsHolding { get; private set; }

    public DisplayAwake()
    {
        timer.Tick += (_, _) =>
        {
            if (until is { } deadline && DateTime.UtcNow >= deadline) Release();
            else if (IsHolding) Apply();
        };
    }

    /// `deadline` null = until released.
    public void Hold(DateTime? deadline)
    {
        if (deadline is { } d && d <= DateTime.UtcNow) { Release(); return; }
        until = deadline;
        IsHolding = true;
        Apply();
        timer.Start();
    }

    public void Release()
    {
        if (!IsHolding) return;
        IsHolding = false;
        until = null;
        timer.Stop();
        Native.SetThreadExecutionState(Native.ExecutionState.Continuous);
    }

    private static void Apply() =>
        Native.SetThreadExecutionState(Native.ExecutionState.Continuous | Native.ExecutionState.DisplayRequired |
                                       Native.ExecutionState.SystemRequired);
}

/// While the LockTune lock screen is up: swallows the Windows key, Alt+Tab,
/// Alt+Esc, Ctrl+Esc, Alt+F4 and Ctrl+Shift+Esc so nobody can get past it
/// without going through the real Windows sign-in. (Ctrl+Alt+Del can't be
/// blocked — the lock screen watches for that and locks Windows instead.)
public sealed class KeyboardGuard : IDisposable
{
    private IntPtr hook = IntPtr.Zero;
    // Keep the delegate alive while hooked.
    private readonly Native.LowLevelKeyboardProc proc;

    /// Raised (on the UI thread) when a blocked shortcut was pressed.
    public event Action? Blocked;

    public KeyboardGuard() { proc = HookProc; }

    public bool IsActive => hook != IntPtr.Zero;

    public void Start()
    {
        if (IsActive) return;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(module?.ModuleName), 0);
    }

    public void Stop()
    {
        if (!IsActive) return;
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (ShouldBlock(info))
            {
                int msg = wParam.ToInt32();
                if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
                    Dispatcher.CurrentDispatcher.BeginInvoke(() => Blocked?.Invoke());
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(Native.KBDLLHOOKSTRUCT info)
    {
        const uint VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SPACE = 0x20;
        const int VK_CONTROL = 0x11, VK_SHIFT = 0x10;
        bool alt = (info.flags & Native.LLKHF_ALTDOWN) != 0;
        bool ctrl = Native.IsKeyDown(VK_CONTROL);
        switch (info.vkCode)
        {
            case VK_LWIN:
            case VK_RWIN:
                return true;
            case VK_TAB when alt:
            case VK_ESCAPE when alt || ctrl:
            case VK_F4 when alt:
            case VK_SPACE when alt:
                return true;
            case VK_ESCAPE when ctrl && Native.IsKeyDown(VK_SHIFT):
                return true;
            default:
                return false;
        }
    }

    public void Dispose() => Stop();
}

/// "Launch at login" through the per-user Run key (no admin needed).
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "LockTune";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(Name, false);
        }
    }

    /// Keeps the Run entry pointing at this exe (after moving or updating it).
    public static void Refresh()
    {
        if (IsEnabled) IsEnabled = true;
    }
}
