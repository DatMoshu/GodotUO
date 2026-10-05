#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>
/// The live mobiles role (<c>--guo-editor-live mobiles</c>, ADR-0027): with a
/// client logged in near EditX,EditY on the private shard, turns the Live
/// layer on and waits for the Shard dock's poll of the bridge's "mobiles" op to
/// report a player there, then checks the layer holds and would draw it.
/// Writes the reply's summary into the live report.
/// </summary>
public partial class EditorSmoke
{
    private void StepLiveMobiles()
    {
        if (ContinueLiveTraffic()) { return; }
        var layers = _world.Layers;
        layers.Live.On = true;
        _world.Visible = true;
        var seen = _shard.LiveMobiles.FirstOrDefault(m => m.Player && m.Facet == _liveFacet
            && System.Math.Abs(m.X - EditX) <= 4 && System.Math.Abs(m.Y - EditY) <= 4);
        if (seen.Name == null)
        {
            return;
        }

        var items = layers.Items("Live", _liveFacet).ToList();
        _live["mobiles_polls"] = _shard.MobilePolls;
        _live["mobiles_reply_count"] = (int)_shard.LastMobiles["count"];
        _live["mobiles_first"] = _shard.LastMobiles["mobiles"][0].ToJsonString();
        _live["mobiles_player_serial"] = seen.Serial;
        _live["mobiles_layer_items"] = items.Count;
        if (!items.Any(i => i.Label == seen.Name))
        {
            _failures.Add("live mobiles: the Live layer does not hold the reported player");
        }

        if (seen.MaxHits <= 0 || seen.Serial == 0)
        {
            _failures.Add("live mobiles: the reply lacks serial or hit points");
        }

        _live["ok"] = _failures.Count == 0;
        if (!CheckLiveTraffic()) { return; }
        Finish();
    }
}
#endif
