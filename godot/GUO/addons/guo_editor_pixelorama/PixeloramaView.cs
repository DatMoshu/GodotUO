#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using Godot;

/// <summary>Windows-only process hosting, using Godot's --wid launch option. Main thread only.</summary>
[Tool]
public partial class PixeloramaView : VBoxContainer
{
    private Control _canvas;
    private Button _open, _detach;
    private Process _process;
    private Process _prepare;
    private Task<string> _prepareOutput, _prepareError;
    private string _pendingPng, _pendingExe;
    private IntPtr _window;
    private IntPtr _originalStyle;
    private double _waiting;
    private Rect2I _lastRect;
    private bool _lastVisible;
    public bool IsEmbedded => _window != IntPtr.Zero;
    public int HostedProcessId => _process?.Id ?? 0;
    public bool HasSession => _process != null || _prepare != null;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Nearest;
        var toolbar = new HBoxContainer();
        AddChild(toolbar);
        toolbar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var theme = EditorInterface.Singleton.GetEditorTheme();
        _open = new Button { Icon = theme.GetIcon("Play", "EditorIcons"), TooltipText = "Open Pixelorama", Flat = true, Disabled = !OperatingSystem.IsWindows() };
        _open.Pressed += () => OpenImage(null);
        toolbar.AddChild(_open);
        _detach = new Button { Icon = theme.GetIcon("ExternalLink", "EditorIcons"), TooltipText = "Detach Pixelorama to its own window (keeps unsaved work)", Flat = true, Disabled = true };
        _detach.Pressed += Detach;
        toolbar.AddChild(_detach);
        _canvas = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_canvas);
        VisibilityChanged += UpdateHost;
    }

    /// <summary>Open one isolated session, optionally with the existing PNG/sidecar exchange contract.</summary>
    public string OpenImage(string png)
    {
        if (!OperatingSystem.IsWindows()) return Report("Embedded Pixelorama currently requires Windows. Use Edit in Pixelorama for a separate window.");
        if (DisplayServer.GetName() == "headless") return Report("Embedding requires a windowed editor.");
        if (HasSession)
        {
            if (string.IsNullOrEmpty(png)) return null;
            // Preserve the current document when opening a different UO asset.
            Detach();
        }
        string exe = ExternalTools.FindPixelorama();
        if (exe == null) return Report("Fetch Pixelorama first: python tools/pixelorama/run.py fetch");
        try
        {
            if (!string.IsNullOrEmpty(png) && !File.Exists(png)) return Report("The exported image no longer exists.");
            var prepare = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", "python"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { Path.Combine(EditorData.RepoRoot, "tools", "pixelorama", "run.py"), "extension", "--install" })
                prepare.ArgumentList.Add(arg);
            _pendingPng = png;
            _pendingExe = exe;
            _prepare = Process.Start(prepare);
            _prepareOutput = _prepare.StandardOutput.ReadToEndAsync();
            _prepareError = _prepare.StandardError.ReadToEndAsync();
            _waiting = 0;
            Report("Preparing the GUO extension…");
            return null;
        }
        catch (Exception ex)
        {
            ReleasePreparation();
            return Report(ex.Message);
        }
    }

    private void LaunchPrepared()
    {
        try
        {
            string png = _pendingPng;
            string exe = _pendingExe;
            long parent = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, GetWindow().GetWindowId());
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            foreach (string arg in new[] { "--wid", parent.ToString(System.Globalization.CultureInfo.InvariantCulture), "--single-window", "--resolution", "1000x700" })
                start.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(png)) start.ArgumentList.Add(Path.GetFullPath(png));
            start.Environment["GUO_ART_EXCHANGE"] = ArtExchange.Root;
            start.Environment.Remove("GUO_ART_SIDECAR");
            start.Environment.Remove("GUO_ART_SOURCE_PNG");
            if (!string.IsNullOrEmpty(png)) start.Environment["GUO_ART_SOURCE_PNG"] = Path.GetFullPath(png);
            if (!string.IsNullOrEmpty(png)) start.Environment["GUO_ART_SIDECAR"] = Path.ChangeExtension(Path.GetFullPath(png), ".json");
            _process = Process.Start(start);
            _waiting = 0;
            Report("Starting Pixelorama…");
        }
        catch (Exception ex)
        {
            _process?.Dispose();
            _process = null;
            Report(ex.Message);
        }
    }

    public override void _Process(double delta)
    {
        if (_prepare != null)
        {
            _waiting += delta;
            if (_prepare.HasExited && _prepareOutput.IsCompleted && _prepareError.IsCompleted)
            {
                int code = _prepare.ExitCode;
                string detail = _prepareError.GetAwaiter().GetResult() + _prepareOutput.GetAwaiter().GetResult();
                ReleasePreparation();
                if (code == 0) LaunchPrepared();
                else { GD.PushWarning(detail); Report("Could not prepare the GUO extension. See Output for details."); }
            }
            else if (_waiting > 30)
            {
                ReleasePreparation();
                Report("Extension preparation timed out. Retry or use the external editor.");
            }
        }
        if (_process == null) return;
        if (_process.HasExited)
        {
            _process.Dispose();
            _process = null;
            _window = IntPtr.Zero;
            Report("Pixelorama closed.");
            return;
        }
        if (_window == IntPtr.Zero)
        {
            _waiting += delta;
            IntPtr parent = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, GetWindow().GetWindowId());
            Native.EnumCallback find = (window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint pid);
                if (pid != _process.Id) return true;
                var className = new StringBuilder(128);
                Native.GetClassNameW(window, className, className.Capacity);
                // Godot's release window class is engine-version dependent. PID is the
                // ownership boundary; --single-window keeps application dialogs inside it.
                if (!Native.IsWindowVisible(window)) return true;
                GD.Print($"[Pixelorama host] candidate pid={pid} class={className}");
                _window = window;
                return false;
            };
            Native.EnumChildWindows(parent, find, IntPtr.Zero);
            if (_window == IntPtr.Zero) Native.EnumWindows(find, IntPtr.Zero);
            if (_window != IntPtr.Zero)
            {
                // --wid creates an owned window on this engine; turn it into an actual child.
                _originalStyle = Native.GetWindowLongPtrW(_window, -16);
                long style = _originalStyle.ToInt64();
                Native.SetWindowLongPtrW(_window, -16, (IntPtr)((style & ~0x80CF0000L) | 0x40000000L));
                Native.SetParent(_window, parent);
                if (Native.GetParent(_window) != parent ||
                    !Native.SetWindowPos(_window, IntPtr.Zero, 0, 0, 1000, 700, 0x0020 | 0x0004 | 0x0010))
                {
                    string error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Detach();
                    Report("Native hosting failed; Pixelorama remains in a separate window. " + error);
                    return;
                }
                _lastRect = default;
                _lastVisible = !IsVisibleInTree();
                Report("Pixelorama embedded. Use Project → GUO: save back to GUO for the selected asset.");
            }
            else if (_waiting > 20)
            {
                Detach();
                Report("No embedded window was found. Pixelorama was left running; use its separate window.");
            }
        }
        UpdateHost();
    }

    private void UpdateHost()
    {
        if (_window == IntPtr.Zero || _canvas == null) return;
        // Screen coordinates include the editor's HiDPI/stretch transform; convert to parent client pixels.
        Vector2 origin = _canvas.GetScreenPosition();
        Vector2 scale = _canvas.GetViewport().GetScreenTransform().Scale;
        var point = new Native.Point { X = (int)origin.X, Y = (int)origin.Y };
        IntPtr parent = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, GetWindow().GetWindowId());
        if (!Native.ScreenToClient(parent, ref point)) return;
        var rect = new Rect2I(point.X, point.Y, Math.Max(1, (int)(_canvas.Size.X * scale.X)), Math.Max(1, (int)(_canvas.Size.Y * scale.Y)));
        bool visible = IsVisibleInTree();
        if (rect != _lastRect)
        {
            if (Native.MoveWindow(_window, rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y, true))
            {
                _lastRect = rect;
                GD.Print($"[Pixelorama host] rect={rect} visible={visible}");
            }
        }
        if (visible != _lastVisible)
            Native.ShowWindow(_window, visible ? 8 : 0); // Show without stealing focus.
        _lastVisible = visible;
    }

    /// <summary>Preserves unsaved documents when the plugin reloads or the editor closes.</summary>
    public void Detach()
    {
        ReleasePreparation();
        if (_window != IntPtr.Zero && Native.IsWindow(_window))
        {
            Native.SetParent(_window, IntPtr.Zero);
            Native.SetWindowLongPtrW(_window, -8, IntPtr.Zero); // Clear the --wid owner too.
            Native.SetWindowLongPtrW(_window, -16, (IntPtr)((_originalStyle.ToInt64() & ~0x40000000L) | 0x00CF0000L));
            Native.SetWindowPos(_window, IntPtr.Zero, 80, 80, 1100, 800, 0x0020 | 0x0004 | 0x0010);
            Native.ShowWindow(_window, 8);
        }
        _window = IntPtr.Zero;
        _process?.Dispose();
        _process = null;
        Report("Detached. Pixelorama keeps running with your unsaved documents.");
    }
    private void ReleasePreparation()
    {
        if (_prepare == null) return;
        // This is only the settings helper, never the artist's Pixelorama process.
        try { if (!_prepare.HasExited) _prepare.Kill(); }
        catch (InvalidOperationException) { }
        _prepare.Dispose();
        _prepare = null;
        _prepareOutput = _prepareError = null;
    }
    private string Report(string text)
    {
        GD.Print("[Pixelorama host] " + text);
        if (_open != null) { _open.TooltipText = text; _open.Disabled = HasSession || !OperatingSystem.IsWindows(); }
        if (_detach != null) _detach.Disabled = !HasSession;
        return text;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        internal delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr window, IntPtr parent);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
#endif
