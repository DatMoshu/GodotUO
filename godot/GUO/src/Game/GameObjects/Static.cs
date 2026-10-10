// SPDX-License-Identifier: BSD-2-Clause

using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Assets;
using GUO.Utility;
using System.Runtime.CompilerServices;

namespace GUO.Game.GameObjects
{
    internal sealed partial class Static : GameObject
    {
        //private static readonly QueuedPool<Static> _pool = new QueuedPool<Static>
        //(
        //    Constants.PREDICTABLE_STATICS,
        //    s =>
        //    {
        //        s.IsDestroyed = false;
        //        s.AlphaHue = 0;
        //        s.FoliageIndex = 0;
        //    }
        //);

        public Static(World world) : base(world) { }

        public string Name => ItemData.Name;

        public ushort OriginalGraphic { get; private set; }

        public ref StaticTiles ItemData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];
        }

        public bool IsVegetation;
        public int Index;


        public static Static Create(World world, ushort graphic, ushort hue, int index)
        {
            Static s = new Static(world); // _pool.GetOne();
            s.Graphic = s.OriginalGraphic = graphic;
            s.Hue = hue;
            s.UpdateGraphicBySeason();
            s.Index = index;

            if (s.ItemData.Height > 5 || s.ItemData.Height == 0)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.IsRoof || s.ItemData.IsSurface && s.ItemData.IsBackground || s.ItemData.IsWall)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.Height == 5 && s.ItemData.IsSurface && !s.ItemData.IsBackground)
            {
                s._canBeTransparent = 1;
            }
            else
            {
                s._canBeTransparent = 0;
            }

            return s;
        }

        public void SetGraphic(ushort g)
        {
            Graphic = g;
        }

        public void RestoreOriginalGraphic()
        {
            Graphic = OriginalGraphic;
        }

        public override void UpdateGraphicBySeason()
        {
            // PORT DEVIATION (GUO): client-side themes repaint matching
            // statics inside their zones through this same hook, ahead of
            // seasons, so season changes and chunk loads re-apply them and
            // toggling a theme off restores the season look by construction.
            // Atlas (PNG) variants keep the original graphic and ride
            // ThemedVariant instead; only legacy numeric variants swap it.
            Assets.VariantAtlas.VariantImage image =
                Assets.VariantAtlas.Resolve(World, X, Y, Assets.VariantKind.Static, OriginalGraphic);
            ThemedVariant = image;
            ushort? themed = image != null
                ? null
                : ThemeManager.StaticVariant(World, X, Y, OriginalGraphic);
            SetGraphic(themed ?? SeasonManager.GetSeasonGraphic(World.Season, OriginalGraphic));
            AllowedToDraw = CanBeDrawn(World, Graphic);
            IsVegetation = StaticFilters.IsVegetation(Graphic);
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
            //_pool.ReturnOne(this);
        }
    }
}