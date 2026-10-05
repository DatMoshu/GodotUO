#if TOOLS
namespace GUO.Editor;

using System;
using System.Threading;
using Godot;

/// <summary>Single editor-wide gate for AI UI, discovery, transports and work.
/// Read the saved preference on the editor thread; workers only use this snapshot/lifetime.
/// Every new AI entry point must require this gate and link cancellable work to its lifetime.</summary>
internal static class AiFeatures
{
    internal const string SettingPath = "guo/ai/enabled";
    internal const string DisabledMessage = "AI features are disabled in Editor Settings > GUO > AI.";
    private static volatile bool _enabled;
    private static CancellationTokenSource _lifetime = new();
    internal static bool Enabled => _enabled;
    internal static CancellationToken Lifetime => _lifetime.Token;

    internal static bool ReadPreference()
    {
        EditorSettings settings = EditorInterface.Singleton.GetEditorSettings();
        if (!settings.HasSetting(SettingPath)) settings.SetSetting(SettingPath, true);
        settings.SetInitialValue(SettingPath, true, false);
        return settings.GetSetting(SettingPath).AsBool();
    }

    internal static void Apply(bool enabled)
    {
        if (_enabled == enabled) return;
        if (enabled)
        {
            _lifetime = new CancellationTokenSource();
            _enabled = true;
        }
        else
        {
            _enabled = false;
            _lifetime.Cancel();
        }
    }

    internal static void RequireEnabled()
    {
        if (!Enabled) throw new InvalidOperationException(DisabledMessage);
    }

    internal static CancellationTokenSource Link(CancellationToken ct)
    {
        RequireEnabled();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        linked.Token.ThrowIfCancellationRequested();
        return linked;
    }
}
#endif
