// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Numerics;
using GUO.Game.Data;

namespace GUO.Game.GameObjects;

internal partial class Mobile
{
    private MobileDrawLayers _drawLayers;

    private sealed class MobileDrawLayers
    {
        private readonly Item[] _items = new Item[PaperdollOrder.N];
        private readonly ushort[] _graphics = new ushort[PaperdollOrder.N];
        private readonly Layer[] _order = new Layer[PaperdollOrder.N];
        private uint _occupied;
        private byte _direction;
        private bool _alt;
        private int _count;

        internal Item this[Layer layer] => _items[(int)layer];

        internal ReadOnlySpan<Layer> Read(Mobile mobile, bool alt, byte direction)
        {
            // Enumerate live items even when the order can be reused. Hue, graphic
            // changes and replacements on the same layer must reach the draw.
            uint occupied = 0;
            bool changed = false;
            for (var link = mobile.Items; link != null; link = link.Next)
            {
                var item = (Item)link;
                int layer = (int)item.Layer;
                if (item.IsDestroyed || layer < (int)Layer.OneHanded || layer > (int)Layer.Legs) continue;
                uint bit = 1u << layer;
                // Match FindItemByLayer when packets temporarily leave duplicate layers.
                if ((occupied & bit) != 0) continue;
                occupied |= bit;
                _items[layer] = item;
                ushort graphic = item.ItemData.AnimID;
                changed |= _graphics[layer] != graphic;
                _graphics[layer] = graphic;
            }

            uint removed = _occupied & ~occupied;
            while (removed != 0)
            {
                int layer = BitOperations.TrailingZeroCount(removed);
                _items[layer] = null;
                _graphics[layer] = 0;
                removed &= removed - 1;
            }

            if (changed || _occupied != occupied || _direction != direction || _alt != alt)
            {
                Span<Layer> raw = stackalloc Layer[PaperdollOrder.N];
                int count = PaperdollOrder.BuildInWorld(_graphics, alt, direction, raw);
                _count = 0;
                foreach (Layer layer in raw[..count])
                    if ((occupied & (1u << (int)layer)) != 0) _order[_count++] = layer;
                _direction = direction;
                _alt = alt;
                _occupied = occupied;
            }
            return _order.AsSpan(0, _count);
        }
    }
}
