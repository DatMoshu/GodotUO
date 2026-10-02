// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the classic login screen's art and layout, read
// at runtime from the player's own client files (nothing of it is shipped).

using System.Collections.Generic;
using Godot;
using GUO.Input.Touch;
using GUO.Utility;

namespace GUO.Pregame3D;

/// <summary>A button as the classic gump has it: its three gumps and where it stands on the 640x480 screen.</summary>
internal readonly record struct GumpButtonArt(ushort Normal, ushort Pressed, ushort Over, Vector2 At);

/// <summary>
/// What LoginBackground and LoginGump draw for this client, as data: the
/// painting (their background gumps composed in their draw order) and where
/// the gump puts each of its pieces. Two branches, as the gump has them:
/// 7.0.64.0 and later (the painting 0x014E), and earlier clients (0x2329 or
/// the tiled stone 0x0E14 with its border and flag).
/// </summary>
internal sealed class Painting
{
    public const int Width = 640, Height = 480;

    public bool Modern { get; }
    public GumpButtonArt Quit { get; }
    public GumpButtonArt Credits { get; }
    public GumpButtonArt Arrow { get; }
    public Rect2 AccountField { get; }
    public Rect2 PasswordField { get; }

    /// <summary>The first checkbox (Autologin); the next ones follow it as the gump lays them out.</summary>
    public Vector2 FirstCheckbox { get; } = new(150, 417);

    public const ushort CheckboxOff = 0x00D2, CheckboxOn = 0x00D3;

    /// <summary>The two version lines, under the panel.</summary>
    public Vector2 VersionAt { get; } = new(286, 453);

    /// <summary>Where the light pools sit on the painting (centre, radius, strength).</summary>
    public IReadOnlyList<Vector4> Lights { get; }

    /// <summary>Older clients draw the login panel (a stone ResizePic) and its labels over the painting.</summary>
    public Rect2? LoginPanel { get; }

    private readonly List<(ushort id, Vector2I at, Vector2I tiled)> _layers = new();

    /// <summary>Every gump of this login screen: the painting's layers and the buttons in all their states.</summary>
    public IEnumerable<ushort> GumpIds
    {
        get
        {
            foreach ((ushort id, Vector2I _, Vector2I _) in _layers)
            {
                yield return id;
            }

            foreach (GumpButtonArt b in new[] { Quit, Credits, Arrow })
            {
                yield return b.Normal;
                yield return b.Pressed;
                yield return b.Over;
            }
        }
    }

    public Painting()
    {
        Modern = Client.Game.UO.Version >= ClientVersion.CV_706400;

        if (Modern)
        {
            // LoginBackground: 0x0150 tiled over 640x480, the flag 0x0151;
            // LoginGump: the painting 0x014E at 0,0 over them.
            _layers.Add((0x0150, Vector2I.Zero, new Vector2I(Width, Height)));
            _layers.Add((0x0151, new Vector2I(0, 4), Vector2I.Zero));
            _layers.Add((0x014E, Vector2I.Zero, Vector2I.Zero));
            Quit = new GumpButtonArt(0x05CA, 0x05C9, 0x05C8, new Vector2(25, 240));
            Credits = new GumpButtonArt(0x05D0, 0x05CF, 0x05CE, new Vector2(530, 125));
            Arrow = new GumpButtonArt(0x05CD, 0x05CC, 0x05CB, new Vector2(280, 365));
            AccountField = new Rect2(218, 283, 210, 30);
            PasswordField = new Rect2(218, 283 + 50, 210, 30);

            // The painting's own light: the open chest's glow and the wall above it.
            Lights = new[] { new Vector4(320, 300, 230, 0.32f), new Vector4(320, 120, 260, 0.18f), new Vector4(70, 250, 120, 0.12f), new Vector4(570, 140, 110, 0.12f) };
        }
        else
        {
            _layers.Add((0x0E14, Vector2I.Zero, new Vector2I(Width, Height)));
            _layers.Add((0x157C, Vector2I.Zero, Vector2I.Zero));
            _layers.Add((0x15A0, new Vector2I(0, 4), Vector2I.Zero));

            if (Client.Game.UO.Version >= ClientVersion.CV_500A)
            {
                _layers.Add((0x2329, Vector2I.Zero, Vector2I.Zero));
            }

            _layers.Add((0x15A0, new Vector2I(0, 4), Vector2I.Zero));
            Quit = new GumpButtonArt(0x1589, 0x158B, 0x158A, new Vector2(555, 4));
            Credits = new GumpButtonArt(0x1583, 0x1585, 0x1584, new Vector2(60, 385));
            Arrow = new GumpButtonArt(0x15A4, 0x15A6, 0x15A5, new Vector2(610, 445));
            AccountField = new Rect2(328, 343, 210, 30);
            PasswordField = new Rect2(328, 343 + 40, 210, 30);
            LoginPanel = new Rect2(128, 288, 451, 157);
            Lights = new[] { new Vector4(320, 200, 300, 0.25f), new Vector4(320, 380, 200, 0.2f), Vector4.Zero, Vector4.Zero };
        }
    }

    /// <summary>
    /// The painting composed, or null while a gump has not reached the atlas
    /// yet (ask again next frame).
    /// </summary>
    public Image Compose()
    {
        Image canvas = Image.CreateEmpty(Width, Height, false, Image.Format.Rgba8);
        canvas.Fill(Colors.Black);

        foreach ((ushort id, Vector2I at, Vector2I tiled) in _layers)
        {
            Image gump = PregameAssets.Gump(id);

            if (gump == null)
            {
                // Not every client has every piece (the flag, a border): only
                // the painting itself is required.
                if (id is 0x014E or 0x2329 or 0x0E14)
                {
                    return null;
                }

                continue;
            }

            if (gump.GetFormat() != Image.Format.Rgba8)
            {
                gump = (Image) gump.Duplicate();
                gump.Convert(Image.Format.Rgba8);
            }

            if (tiled == Vector2I.Zero)
            {
                canvas.BlendRect(gump, new Rect2I(Vector2I.Zero, gump.GetSize()), at);
                continue;
            }

            for (int y = 0; y < tiled.Y; y += gump.GetHeight())
            {
                for (int x = 0; x < tiled.X; x += gump.GetWidth())
                {
                    canvas.BlendRect(gump, new Rect2I(0, 0, System.Math.Min(gump.GetWidth(), tiled.X - x), System.Math.Min(gump.GetHeight(), tiled.Y - y)), at + new Vector2I(x, y));
                }
            }
        }

        return canvas;
    }
}
