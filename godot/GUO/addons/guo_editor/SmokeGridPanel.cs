#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Synthetic grid fixture: no client images or loader calls.</summary>
[Tool]
public partial class SmokeGridPanel : GridPanel
{
    public int IdCount { get; set; } = 500;
    public int IconCalls { get; private set; }
    public int IdScans { get; private set; }
    public int Descriptions { get; private set; }
    private Image _icon;

    public override string SmokeQuery => "260";
    protected override IEnumerable<int> Ids() { IdScans++; return Enumerable.Range(0, IdCount); }
    protected override string Caption(int id) => $"{(id % 2 == 0 ? "even" : "odd")} fixture {id}";
    protected override Image Icon(int id)
    {
        IconCalls++;
        if (_icon == null)
        {
            _icon = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            _icon.Fill(Colors.White);
        }
        return _icon;
    }
    protected override Inspection Describe(int id)
    {
        Descriptions++;
        return new Inspection { Source = "Synthetic grid", Id = id.ToString(), Text = Caption(id) };
    }
    public void ResetCounts() { IconCalls = 0; IdScans = 0; Descriptions = 0; }
    public override void Shutdown() { _icon?.Dispose(); _icon = null; }
}
#endif
