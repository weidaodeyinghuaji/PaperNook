using System.Runtime.InteropServices;
using System.Text;

namespace PaperTodo;

internal static class FullscreenForegroundWindowDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int FullscreenTolerance = 2;
    private const int MinCandidateSize = 160;
    private const long ForegroundHandoffContinuationMilliseconds = 1000;
    private static IntPtr _foregroundSessionWindow;
    private static uint _foregroundSessionProcessId;
    private static IntPtr _trackedFullscreenWindow;
    private static readonly HashSet<IntPtr> _ignoredSessionFullscreenWindows = new();
    private static long _lastRelatedForegroundTick;
    private static IntPtr _temporaryContinuationWindow;
    private static long _temporaryContinuationStartedTick;

    public static bool TryGetFullscreenWindow(out IntPtr fullscreenWindow, bool allowGlobalScan)
    {
        fullscreenWindow = IntPtr.Zero;
        var foreground = GetForegroundWindow();
        var shellWindow = GetShellWindow();
        if (foreground == IntPtr.Zero)
        {
            ClearTrackingState();
            return false;
        }

        if (IsCurrentProcessWindow(foreground))
        {
            var canContinue = IsWithinHandoffWindow(_lastRelatedForegroundTick, Environment.TickCount64);
            if (canContinue &&
                TryGetTrackedFullscreenWindow(shellWindow, out fullscreenWindow))
            {
                return true;
            }

            if (!canContinue)
            {
                ClearTrackingState();
            }

            return false;
        }

        var isCandidateForeground = IsCandidateExternalWindow(foreground, shellWindow);
        if (isCandidateForeground && IsFullscreenWindow(foreground))
        {
            ClearForegroundSession();
            _trackedFullscreenWindow = foreground;
            _lastRelatedForegroundTick = Environment.TickCount64;
            fullscreenWindow = foreground;
            return true;
        }

        var isForegroundSessionWindow = _foregroundSessionWindow != IntPtr.Zero &&
            foreground == _foregroundSessionWindow &&
            ProcessIdFor(foreground) == _foregroundSessionProcessId;
        if (isForegroundSessionWindow ||
            TryContinueThroughTemporaryOwnedWindow(
                foreground,
                GetAncestor(_foregroundSessionWindow, GaRootOwner)))
        {
            if (isForegroundSessionWindow)
            {
                ClearTemporaryContinuation();
                _lastRelatedForegroundTick = Environment.TickCount64;
            }

            CleanupIgnoredSessionFullscreenWindows(shellWindow);
            if (TryGetTrackedFullscreenWindow(shellWindow, out fullscreenWindow))
            {
                return true;
            }

            return allowGlobalScan &&
                   TryGetNewSessionFullscreenWindow(shellWindow, out fullscreenWindow);
        }

        if (TryGetTrackedFullscreenWindow(shellWindow, out var trackedFullscreenWindow) &&
            TryContinueThroughTemporaryOwnedWindow(
                foreground,
                GetAncestor(trackedFullscreenWindow, GaRootOwner)))
        {
            fullscreenWindow = trackedFullscreenWindow;
            return true;
        }

        if (!isCandidateForeground)
        {
            ClearTrackingState();
            return false;
        }

        var foregroundProcessId = ProcessIdFor(foreground);
        if (foregroundProcessId == 0)
        {
            ClearTrackingState();
            return false;
        }

        StartForegroundSession(foreground, foregroundProcessId, shellWindow);
        return false;
    }


    private static bool TryGetTrackedFullscreenWindow(IntPtr shellWindow, out IntPtr fullscreenWindow)
    {
        fullscreenWindow = IntPtr.Zero;
        if (_trackedFullscreenWindow == IntPtr.Zero)
        {
            return false;
        }

        if (!IsCandidateExternalWindow(_trackedFullscreenWindow, shellWindow) ||
            !IsFullscreenWindow(_trackedFullscreenWindow))
        {
            _trackedFullscreenWindow = IntPtr.Zero;
            return false;
        }

        fullscreenWindow = _trackedFullscreenWindow;
        return true;
    }

    private static void StartForegroundSession(IntPtr foreground, uint foregroundProcessId, IntPtr shellWindow)
    {
        _trackedFullscreenWindow = IntPtr.Zero;
        ClearForegroundSession();
        _foregroundSessionWindow = foreground;
        _foregroundSessionProcessId = foregroundProcessId;
        _lastRelatedForegroundTick = Environment.TickCount64;

        // Existing fullscreen siblings are background history for this normal-window session.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != foreground &&
                IsSessionFullscreenCandidate(hwnd, shellWindow))
            {
                _ignoredSessionFullscreenWindows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static bool TryGetNewSessionFullscreenWindow(IntPtr shellWindow, out IntPtr fullscreenWindow)
    {
        fullscreenWindow = IntPtr.Zero;
        if (_foregroundSessionProcessId == 0)
        {
            return false;
        }

        var foundWindow = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (_ignoredSessionFullscreenWindows.Contains(hwnd) ||
                !IsSessionFullscreenCandidate(hwnd, shellWindow))
            {
                return true;
            }

            foundWindow = hwnd;
            return false;
        }, IntPtr.Zero);

        _trackedFullscreenWindow = foundWindow;
        fullscreenWindow = foundWindow;
        return foundWindow != IntPtr.Zero;
    }

    private static bool IsSessionFullscreenCandidate(IntPtr hwnd, IntPtr shellWindow)
    {
        return ProcessIdFor(hwnd) == _foregroundSessionProcessId &&
               IsCandidateExternalWindow(hwnd, shellWindow) &&
               IsFullscreenWindow(hwnd);
    }

    private static void CleanupIgnoredSessionFullscreenWindows(IntPtr shellWindow)
    {
        if (_ignoredSessionFullscreenWindows.Count == 0)
        {
            return;
        }

        _ignoredSessionFullscreenWindows.RemoveWhere(hwnd =>
            !IsSessionFullscreenCandidate(hwnd, shellWindow));
    }

    private static bool TryContinueThroughTemporaryOwnedWindow(IntPtr window, IntPtr rootOwner)
    {
        if (!IsTemporaryOwnedWindowForRootOwner(window, rootOwner))
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (_temporaryContinuationWindow != window)
        {
            _temporaryContinuationWindow = window;
            _temporaryContinuationStartedTick = now;
            _lastRelatedForegroundTick = now;
        }

        return IsWithinHandoffWindow(_temporaryContinuationStartedTick, now);
    }

    private static bool IsTemporaryOwnedWindowForRootOwner(IntPtr window, IntPtr rootOwner)
    {
        if (window == IntPtr.Zero || rootOwner == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(window, GaRoot);
        return root != IntPtr.Zero &&
               root != rootOwner &&
               GetAncestor(window, GaRootOwner) == rootOwner;
    }

    private static bool IsWithinHandoffWindow(long startedAt, long now)
    {
        if (startedAt == 0)
        {
            return false;
        }

        var elapsed = now - startedAt;
        return elapsed >= 0 && elapsed <= ForegroundHandoffContinuationMilliseconds;
    }

    private static void ClearTemporaryContinuation()
    {
        _temporaryContinuationWindow = IntPtr.Zero;
        _temporaryContinuationStartedTick = 0;
    }

    private static void ClearTrackingState()
    {
        _trackedFullscreenWindow = IntPtr.Zero;
        ClearForegroundSession();
        _lastRelatedForegroundTick = 0;
    }

    private static void ClearForegroundSession()
    {
        _foregroundSessionWindow = IntPtr.Zero;
        _foregroundSessionProcessId = 0;
        _ignoredSessionFullscreenWindows.Clear();
        ClearTemporaryContinuation();
    }

    private static bool IsFullscreenWindow(IntPtr hwnd)
    {
        if (TryGetDwmWindowBounds(hwnd, out var dwmRect) &&
            TryGetMonitorInfoForRect(dwmRect, out var dwmMonitorInfo) &&
            IsFullscreenRect(dwmRect, dwmMonitorInfo.Monitor))
        {
            return true;
        }

        return TryGetRawWindowBounds(hwnd, out var rawRect) &&
               TryGetMonitorInfoForRect(rawRect, out var rawMonitorInfo) &&
               IsFullscreenRect(rawRect, rawMonitorInfo.Monitor);
    }

    private static bool TryGetMonitorInfoForRect(Rectangle rect, out MonitorInfo monitorInfo)
    {
        monitorInfo = default;
        if (rect.IsEmpty ||
            rect.Width < MinCandidateSize ||
            rect.Height < MinCandidateSize)
        {
            return false;
        }

        var windowRect = rect;
        var monitor = MonitorFromRect(ref windowRect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        return GetMonitorInfo(monitor, ref monitorInfo);
    }

    private static bool IsFullscreenRect(Rectangle windowRect, Rectangle monitorRect)
    {
        return CoversMonitor(windowRect, monitorRect);
    }

    private static bool IsCandidateExternalWindow(IntPtr hwnd, IntPtr shellWindow)
    {
        if (hwnd == IntPtr.Zero ||
            hwnd == shellWindow ||
            !IsWindow(hwnd) ||
            IsCurrentProcessWindow(hwnd) ||
            !IsVisibleWindow(hwnd) ||
            IsToolWindow(hwnd) ||
            IsCloaked(hwnd) ||
            IsShellClassWindow(hwnd))
        {
            return false;
        }

        return true;
    }

    private static bool IsVisibleWindow(IntPtr hwnd)
    {
        return IsWindowVisible(hwnd) && !IsIconic(hwnd);
    }

    private static bool IsToolWindow(IntPtr hwnd)
    {
        return (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool IsShellClassWindow(IntPtr hwnd)
    {
        var className = GetWindowClassName(hwnd);
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool TryGetDwmWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rectangle>()) == 0 &&
               !rect.IsEmpty;
    }

    private static bool TryGetRawWindowBounds(IntPtr hwnd, out Rectangle rect)
    {
        return GetWindowRect(hwnd, out rect) && !rect.IsEmpty;
    }

    private static bool IsCurrentProcessWindow(IntPtr hwnd)
    {
        return ProcessIdFor(hwnd) == Environment.ProcessId;
    }

    private static uint ProcessIdFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    private static bool CoversMonitor(Rectangle windowRect, Rectangle monitorRect)
    {
        return windowRect.Left <= monitorRect.Left + FullscreenTolerance &&
               windowRect.Top <= monitorRect.Top + FullscreenTolerance &&
               windowRect.Right >= monitorRect.Right - FullscreenTolerance &&
               windowRect.Bottom >= monitorRect.Bottom - FullscreenTolerance;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length <= 0 ? string.Empty : builder.ToString(0, length);
    }


    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);


    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();


    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rectangle lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rectangle lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);




    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Rectangle pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public bool IsEmpty => Right <= Left || Bottom <= Top;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle Monitor;
        public Rectangle WorkArea;
        public uint Flags;
    }
}
