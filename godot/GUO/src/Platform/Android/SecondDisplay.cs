// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using Godot;

namespace GUO.Platform.Android
{
    /// <summary>
    /// A second physical display on an Android device, reached through the
    /// engine's own Java bridge: no Gradle build, no plugin AAR.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream has nothing of the kind; ClassicUO is a
    /// one-window desktop client. Everything here is Godot 4.7's
    /// <c>JavaClassWrapper</c> and the <c>AndroidRuntime</c> singleton that
    /// every exported Android template carries (see ADR-0009 for what was
    /// measured). The class talks to three Android objects:
    ///
    ///   * <c>DisplayManager.getDisplays(DISPLAY_CATEGORY_PRESENTATION)</c>
    ///     is the capability check -- a display an app may open a
    ///     <c>Presentation</c> on. The AYN Thor's lower screen is one; a phone
    ///     has none, and then <see cref="Find"/> returns null and nothing
    ///     else in this file runs.
    ///   * an <c>android.app.Presentation</c> holding one <c>ImageView</c>,
    ///     created and shown on Android's UI thread, which is the only thread
    ///     a Dialog can be built on (its constructor makes a Handler).
    ///   * a <c>Bitmap</c> the ImageView shows, refilled from the Godot side
    ///     with <c>copyPixelsFromBuffer</c>: RGBA8 bytes as Godot's Image
    ///     lays them out are exactly ARGB_8888's memory order, so the frame
    ///     crosses with two copies and no per-pixel work. The drawable's
    ///     bitmap filter is switched off so the integer scale up to the
    ///     display stays nearest-neighbour (rule 7).
    ///
    /// Touch on that display reaches the view as MotionEvents on the UI
    /// thread; they are read there into <see cref="TouchEvent"/>s and queued
    /// for the Godot thread, which is the only thread the client's input may
    /// run on. Every Java call is wrapped so a missing method on some other
    /// vendor's Android turns into <see cref="LastError"/> and a disabled
    /// feature rather than an exception in the frame.
    ///
    /// Everything is guarded on <see cref="IsSupported"/>; on any platform
    /// but Android this class is inert.
    /// </remarks>
    internal sealed class SecondDisplay
    {
        /// <summary>Android's DisplayManager.DISPLAY_CATEGORY_PRESENTATION.</summary>
        private const string PresentationCategory = "android.hardware.display.category.PRESENTATION";

        /// <summary>One finger event from the second display, in its own physical pixels.</summary>
        public readonly struct TouchEvent
        {
            public TouchEvent(int action, int pointerId, float x, float y)
            {
                Action = action;
                PointerId = pointerId;
                X = x;
                Y = y;
            }

            /// <summary>MotionEvent.ACTION_DOWN (0), _UP (1), _MOVE (2), _CANCEL (3), _POINTER_DOWN (5), _POINTER_UP (6).</summary>
            public int Action { get; }

            public int PointerId { get; }

            public float X { get; }

            public float Y { get; }
        }

        private readonly GodotObject _runtime;
        private readonly GodotObject _activity;
        private readonly GodotObject _display;

        private GodotObject _presentation;
        private GodotObject _view;
        private GodotObject _bitmap;
        private GodotObject _byteBufferClass;
        private GodotObject _touchListener;
        private Callable _openCallable;
        private Callable _closeCallable;
        private int _bitmapWidth, _bitmapHeight;

        private readonly ConcurrentQueue<TouchEvent> _touches = new();

        private SecondDisplay(GodotObject runtime, GodotObject activity, GodotObject display)
        {
            _runtime = runtime;
            _activity = activity;
            _display = display;

            DisplayId = display.Call("getDisplayId").AsInt32();
            Name = display.Call("getName").AsString();

            // Physical size as the panel reports it, then turned the way the
            // display is currently rotated: what a Presentation window gets.
            GodotObject mode = display.Call("getMode").AsGodotObject();
            int w = mode.Call("getPhysicalWidth").AsInt32();
            int h = mode.Call("getPhysicalHeight").AsInt32();
            int rotation = display.Call("getRotation").AsInt32();

            if (rotation == 1 || rotation == 3)
            {
                (w, h) = (h, w);
            }

            Width = w;
            Height = h;
            Rotation = rotation;
        }

        /// <summary>Whether this platform has the Java bridge at all.</summary>
        public static bool IsSupported =>
            OperatingSystem.IsAndroid() && Engine.HasSingleton("AndroidRuntime");

        public int DisplayId { get; }

        public string Name { get; }

        /// <summary>Physical pixels, as the display is currently rotated.</summary>
        public int Width { get; }

        /// <summary>Physical pixels, as the display is currently rotated.</summary>
        public int Height { get; }

        public int Rotation { get; }

        /// <summary>The presentation is on the display and takes frames.</summary>
        public volatile bool Ready;

        /// <summary>What went wrong last, for the log and the doctor.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>
        /// The first display Android will let this app present on, or null.
        /// </summary>
        public static SecondDisplay Find()
        {
            if (!IsSupported)
            {
                return null;
            }

            try
            {
                GodotObject runtime = Engine.GetSingleton("AndroidRuntime");
                GodotObject activity = runtime.Call("getActivity").AsGodotObject();

                if (activity == null)
                {
                    GD.Print("[GUO] dual screen: no activity yet");

                    return null;
                }

                GodotObject manager = activity.Call("getSystemService", "display").AsGodotObject();
                Godot.Collections.Array displays = manager.Call("getDisplays", PresentationCategory).AsGodotArray();

                GD.Print($"[GUO] dual screen: {displays.Count} presentation display(s)");

                // A second screen is one you can touch. Some handhelds list a
                // presentation display with no panel behind it: the Odin 2
                // Mini's firmware reports a 1920x1080 "Built-in Screen" on a
                // second port with no touch input (dumpsys: touch NONE), and
                // presenting there sent the shelf gumps nowhere. The Thor's
                // lower screen has its own touchscreen, so there are two.
                int touchscreens = CountTouchscreens();

                if (displays.Count > 0 && touchscreens < 2)
                {
                    GD.Print($"[GUO] dual screen: {touchscreens} touchscreen(s), so the presentation display has none; staying single-screen");

                    return null;
                }

                foreach (Variant v in displays)
                {
                    GodotObject display = v.AsGodotObject();

                    if (display == null)
                    {
                        continue;
                    }

                    var found = new SecondDisplay(runtime, activity, display);

                    GD.Print(
                        $"[GUO] dual screen: display {found.DisplayId} \"{found.Name}\" "
                        + $"{found.Width}x{found.Height} rotation {found.Rotation}"
                    );

                    return found;
                }
            }
            catch (Exception e)
            {
                GD.PrintErr($"[GUO] dual screen: detection failed: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Touchscreen input devices (InputDevice.SOURCE_TOUCHSCREEN), virtual
        /// ones excluded. Errs high: if the count cannot be read it answers 2,
        /// the old behaviour.
        /// </summary>
        private static int CountTouchscreens()
        {
            const int SourceTouchscreen = 0x00001002;

            try
            {
                JavaClass inputDevice = JavaClassWrapper.Wrap("android.view.InputDevice");
                int[] ids = inputDevice.Call("getDeviceIds").AsInt32Array();
                int count = 0;

                foreach (int id in ids)
                {
                    GodotObject device = inputDevice.Call("getDevice", id).AsGodotObject();

                    if (device != null && !device.Call("isVirtual").AsBool() && device.Call("supportsSource", SourceTouchscreen).AsBool())
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception e)
            {
                GD.PrintErr($"[GUO] dual screen: could not count touchscreens ({e.Message}); assuming a touchable second screen");

                return 2;
            }
        }

        /// <summary>
        /// Open a full-screen presentation on the display, showing a bitmap of
        /// the given size stretched to the display. Returns at once; the work
        /// happens on Android's UI thread and <see cref="Ready"/> says when it
        /// is done.
        /// </summary>
        public void Open(int bitmapWidth, int bitmapHeight)
        {
            _bitmapWidth = bitmapWidth;
            _bitmapHeight = bitmapHeight;

            // The Callable has to outlive the call: Java holds the Runnable,
            // and the Runnable holds the Callable's native side.
            _openCallable = Callable.From(OpenOnUiThread);

            GodotObject runnable = _runtime.Call("createRunnableFromGodotCallable", _openCallable).AsGodotObject();
            _activity.Call("runOnUiThread", runnable);
        }

        /// <summary>Runs on Android's UI thread.</summary>
        private void OpenOnUiThread()
        {
            try
            {
                JavaClass Presentation = JavaClassWrapper.Wrap("android.app.Presentation");
                JavaClass ImageView = JavaClassWrapper.Wrap("android.widget.ImageView");
                JavaClass ScaleType = JavaClassWrapper.Wrap("android.widget.ImageView$ScaleType");
                JavaClass Bitmap = JavaClassWrapper.Wrap("android.graphics.Bitmap");
                JavaClass BitmapConfig = JavaClassWrapper.Wrap("android.graphics.Bitmap$Config");

                _byteBufferClass = JavaClassWrapper.Wrap("java.nio.ByteBuffer");

                // Presentation(Context, Display): the constructor is called by
                // the class's own name, as JavaClassWrapper spells it.
                _presentation = Presentation.Call("Presentation", _activity, _display).AsGodotObject();
                Check("Presentation()");

                _view = ImageView.Call("ImageView", _activity).AsGodotObject();
                Check("ImageView()");

                _view.Call("setScaleType", ScaleType.Call("valueOf", "FIT_XY").AsGodotObject());
                _view.Call("setBackgroundColor", unchecked((int)0xFF000000));

                GodotObject argb = BitmapConfig.Call("valueOf", "ARGB_8888").AsGodotObject();
                _bitmap = Bitmap.Call("createBitmap", _bitmapWidth, _bitmapHeight, argb).AsGodotObject();
                Check("Bitmap.createBitmap");

                _view.Call("setImageBitmap", _bitmap);

                // Rule 7: the bitmap is scaled up to the panel by an integer
                // factor and must not be filtered on the way.
                GodotObject drawable = _view.Call("getDrawable").AsGodotObject();
                drawable?.Call("setFilterBitmap", false);

                _touchListener = JavaClassWrapper.CreateSamCallback(
                    "android.view.View$OnTouchListener",
                    Callable.From<GodotObject, GodotObject, bool>(OnTouch)
                );

                if (_touchListener != null)
                {
                    _view.Call("setOnTouchListener", _touchListener);
                }
                else
                {
                    GD.PrintErr("[GUO] dual screen: could not make the touch listener; the second screen will show but not take touch");
                }

                _presentation.Call("setContentView", _view);

                GodotObject window = _presentation.Call("getWindow").AsGodotObject();

                // MATCH_PARENT twice: a Dialog window wraps its content otherwise.
                window.Call("setLayout", -1, -1);
                // FLAG_FULLSCREEN (0x400) | FLAG_KEEP_SCREEN_ON (0x80).
                window.Call("addFlags", 0x400 | 0x80);

                _presentation.Call("show");
                Check("Presentation.show");

                Ready = true;

                GD.Print($"[GUO] dual screen: presentation shown on display {DisplayId}, bitmap {_bitmapWidth}x{_bitmapHeight}");
            }
            catch (Exception e)
            {
                LastError = e.Message;
                GD.PrintErr($"[GUO] dual screen: open failed: {e}");
            }
        }

        /// <summary>
        /// Push a frame. <paramref name="rgba"/> is width*height*4 bytes in
        /// RGBA8 order, which is ARGB_8888's memory order. Godot thread.
        /// </summary>
        public bool Present(byte[] rgba)
        {
            if (!Ready || _bitmap == null)
            {
                return false;
            }

            if (rgba.Length != _bitmapWidth * _bitmapHeight * 4)
            {
                LastError = $"frame is {rgba.Length} bytes, bitmap wants {_bitmapWidth * _bitmapHeight * 4}";

                return false;
            }

            try
            {
                GodotObject buffer = _byteBufferClass.Call("wrap", rgba).AsGodotObject();
                _bitmap.Call("copyPixelsFromBuffer", buffer);

                // Thread-safe by contract; the redraw lands on the UI thread.
                _view.Call("postInvalidate");

                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;

                return false;
            }
        }

        /// <summary>
        /// Called by Android on its UI thread for every MotionEvent on the
        /// view. Reads what the client needs and queues it; nothing of the
        /// client is touched from here.
        /// </summary>
        private bool OnTouch(GodotObject view, GodotObject motion)
        {
            try
            {
                int action = motion.Call("getActionMasked").AsInt32();
                int index = motion.Call("getActionIndex").AsInt32();

                if (action == 2)
                {
                    // ACTION_MOVE reports every pointer at once.
                    int count = motion.Call("getPointerCount").AsInt32();

                    for (int i = 0; i < count; i++)
                    {
                        _touches.Enqueue(new TouchEvent(
                            action,
                            motion.Call("getPointerId", i).AsInt32(),
                            (float)motion.Call("getX", i).AsDouble(),
                            (float)motion.Call("getY", i).AsDouble()
                        ));
                    }
                }
                else
                {
                    _touches.Enqueue(new TouchEvent(
                        action,
                        motion.Call("getPointerId", index).AsInt32(),
                        (float)motion.Call("getX", index).AsDouble(),
                        (float)motion.Call("getY", index).AsDouble()
                    ));
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }

            return true;
        }

        /// <summary>Take the queued finger events. Godot thread.</summary>
        public bool TryDequeueTouch(out TouchEvent e)
        {
            return _touches.TryDequeue(out e);
        }

        public void Close()
        {
            if (_presentation == null)
            {
                return;
            }

            Ready = false;

            try
            {
                _closeCallable = Callable.From(() =>
                {
                    try
                    {
                        _presentation?.Call("dismiss");
                    }
                    catch (Exception e)
                    {
                        LastError = e.Message;
                    }
                });

                GodotObject runnable = _runtime.Call("createRunnableFromGodotCallable", _closeCallable).AsGodotObject();
                _activity.Call("runOnUiThread", runnable);
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
        }

        /// <summary>Turn a pending Java exception into a C# one, with its message.</summary>
        private static void Check(string what)
        {
            JavaObject exception = JavaClassWrapper.GetException();

            if (exception != null)
            {
                string message = "";

                try
                {
                    message = exception.Call("getMessage").AsString();
                }
                catch
                {
                    // The message is a nicety.
                }

                throw new InvalidOperationException($"{what}: {exception.GetJavaClass()?.GetJavaClassName()} {message}");
            }
        }
    }
}
