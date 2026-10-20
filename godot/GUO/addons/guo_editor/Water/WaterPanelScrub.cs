// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using Godot;
using GUO.Store;

internal partial class WaterPanel
{
    private string _scrubKey;
    private StoreWater _scrubStart;
    private DraftDocument _scrubDocument;
    private Action _stopScrub;
    private bool ScrubChanged => _scrubKey != null
        && !WaterAuthoring.Serialize(_style).AsSpan().SequenceEqual(WaterAuthoring.Serialize(_scrubStart));

    private void AttachWaterScrub(Control label, SpinBox input, string key)
    {
        Action stop = null;
        stop = NumericScrub.Attach(label, input, () =>
        {
            EndNumericScrub();
            if (!input.Editable) { stop(); return; }
            _scrubKey = key; _scrubStart = _style; _scrubDocument = _document; _stopScrub = stop;
        }, EndNumericScrub);
        label.TooltipText += " One label drag is one Undo step.";
    }

    private void ChangeNumber(string key, StoreWater style)
    {
        if (_scrubKey == key && ReferenceEquals(_scrubDocument, _document))
        {
            WaterAuthoring.Serialize(style);
            _style = style; Sync();
        }
        else Change(style);
    }

    private void EndNumericScrub() => FinishNumericScrub(true);
    private void FinishNumericScrub(bool refresh)
    {
        if (_scrubKey == null) return;
        bool changed = ScrubChanged;
        StoreWater final = _style, start = _scrubStart;
        DraftDocument document = _scrubDocument;
        Action stop = _stopScrub;
        _scrubKey = null; _scrubStart = null; _scrubDocument = null; _stopScrub = null;
        stop?.Invoke();
        if (changed)
        {
            _style = start;
            Push(final, document, refresh);
        }
        else if (refresh) Sync();
    }
}
#endif
