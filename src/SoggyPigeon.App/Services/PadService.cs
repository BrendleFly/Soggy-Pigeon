using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace SoggyPigeon.App.Services;

/// <summary>Gamepad buttons the app reacts to (§6.2: controller alongside mouse).</summary>
public enum PadButton { Up, Down, Left, Right, A, B, Start }

/// <summary>
/// Pads to UI actions: d-pad/stick moves focus (with key repeat), A activates,
/// B goes back, Start presses the page's primary action. Windows: XInput.
/// Linux: the joystick API (/dev/input/js*). Mouse remains first-class; this
/// service only translates pad state into button events.
/// </summary>
public sealed class PadService : IDisposable
{
    public event Action<PadButton>? Pressed;

    private static readonly bool DebugLog = Environment.GetEnvironmentVariable("SP_PAD_DEBUG") == "1";

    /// <summary>Append a line to %TEMP%\sp-pad.log when SP_PAD_DEBUG=1.</summary>
    internal static void Log(string message)
    {
        if (!DebugLog) return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "sp-pad.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
    }

    private Thread? _thread;
    private volatile bool _stop;

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "pad-poll" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(500); } catch { }
        _thread = null;
        GC.SuppressFinalize(this);
    }

    private void PollLoop()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // SDL2 first: it recognizes HID pads (Stadia, DualShock, etc.) that
            // XInput cannot see at all. XInput stays as the fallback.
            if (new SdlPad(b => Pressed?.Invoke(b)).Loop(ref _stop)) return;
            new XInputPad(b => Pressed?.Invoke(b)).Loop(ref _stop);
        }
        else
        {
            new LinuxJsPad(b => Pressed?.Invoke(b)).Loop(ref _stop);
        }
    }

    // ---- shared polling frame ---------------------------------------------

    private abstract class PadBase
    {
        protected readonly Action<PadButton> Emit;
        protected PadBase(Action<PadButton> emit) { Emit = emit; }

        private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(320);
        private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(130);

        /// <summary>Edge + repeat state machine; call with the raw direction/button states each frame.</summary>
        protected void Frame(bool up, bool down, bool left, bool right, bool a, bool b, bool start)
        {
            Step(ref _up, ref _upNext, up, PadButton.Up);
            Step(ref _down, ref _downNext, down, PadButton.Down);
            Step(ref _left, ref _leftNext, left, PadButton.Left);
            Step(ref _right, ref _rightNext, right, PadButton.Right);
            StepNoRepeat(ref _a, a, PadButton.A);
            StepNoRepeat(ref _b, b, PadButton.B);
            StepNoRepeat(ref _start, start, PadButton.Start);
        }

        private bool _up, _down, _left, _right, _a, _b, _start;
        private DateTime _upNext, _downNext, _leftNext, _rightNext;

        private void Step(ref bool held, ref DateTime nextAt, bool now, PadButton btn)
        {
            var t = DateTime.UtcNow;
            if (now)
            {
                if (!held) { held = true; nextAt = t + RepeatDelay; Emit(btn); }
                else if (t >= nextAt) { nextAt = t + RepeatRate; Emit(btn); }
            }
            else held = false;
        }

        private void StepNoRepeat(ref bool held, bool now, PadButton btn)
        {
            if (now && !held) { held = true; Emit(btn); }
            else if (!now) held = false;
        }
    }

    // ---- Windows: SDL2 game controller (recognizes HID pads) ----------------

    private sealed class SdlPad : PadBase
    {
        private const int INIT_EVENTS = 0x4000, INIT_GAMECONTROLLER = 0x2000;
        private const uint EVT_AXIS = 0x650, EVT_BUTTONDOWN = 0x651, EVT_BUTTONUP = 0x652, EVT_DEVICEADDED = 0x653;
        private const int BTN_A = 0, BTN_B = 1, BTN_START = 6,
                          DPAD_UP = 11, DPAD_DOWN = 12, DPAD_LEFT = 13, DPAD_RIGHT = 14;
        private const short STICK = 16000;

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_Init(uint flags);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_Quit();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_PollEvent(IntPtr evnt);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_NumJoysticks();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SDL_IsGameController(int index);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerOpen(int index);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_GameControllerClose(IntPtr pad);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerName(IntPtr pad);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetError();

        private readonly List<IntPtr> _open = new();

        public SdlPad(Action<PadButton> emit) : base(emit) { }

        public bool Loop(ref bool stop)
        {
            try
            {
                if (SDL_Init(INIT_EVENTS | INIT_GAMECONTROLLER) != 0)
                {
                    Log($"SDL_Init failed: {Marshal.PtrToStringAnsi(SDL_GetError())}");
                    return false;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log($"SDL2.dll unavailable ({ex.GetType().Name}) — falling back to XInput");
                return false;
            }

            try
            {
                OpenConnectedPads();
                if (_open.Count == 0) Log("SDL: no game controllers detected");

                var evt = Marshal.AllocHGlobal(64);
                var up = false; var down = false; var left = false; var right = false;
                var a = false; var b = false; var start = false;

                while (!stop)
                {
                    while (SDL_PollEvent(evt) != 0)
                    {
                        var type = (uint)Marshal.ReadInt32(evt);
                        switch (type)
                        {
                            case EVT_BUTTONDOWN:
                            case EVT_BUTTONUP:
                            {
                                var pressed = type == EVT_BUTTONDOWN;
                                var button = Marshal.ReadByte(evt, 12);
                                switch (button)
                                {
                                    case BTN_A: a = pressed; break;
                                    case BTN_B: b = pressed; break;
                                    case BTN_START: start = pressed; break;
                                    case DPAD_UP: up = pressed; break;
                                    case DPAD_DOWN: down = pressed; break;
                                    case DPAD_LEFT: left = pressed; break;
                                    case DPAD_RIGHT: right = pressed; break;
                                }
                                break;
                            }
                            case EVT_AXIS:
                            {
                                var axis = Marshal.ReadByte(evt, 12);
                                var value = Marshal.ReadInt16(evt, 16);   // pad to 2-byte alignment
                                if (axis == 0) { left = value < -STICK; right = value > STICK; }
                                else if (axis == 1) { up = value < -STICK; down = value > STICK; }
                                break;
                            }
                            case EVT_DEVICEADDED:
                                OpenConnectedPads();
                                break;
                        }
                    }
                    Frame(up, down, left, right, a, b, start);
                    Thread.Sleep(8);
                }
                return true;
            }
            finally
            {
                foreach (var pad in _open)
                {
                    try { SDL_GameControllerClose(pad); } catch { }
                }
                try { SDL_Quit(); } catch { }
            }
        }

        private void OpenConnectedPads()
        {
            var count = SDL_NumJoysticks();
            for (var i = 0; i < count; i++)
            {
                if (SDL_IsGameController(i) == 0) continue;
                var handle = SDL_GameControllerOpen(i);
                if (handle == IntPtr.Zero) continue;
                if (!_open.Contains(handle))
                {
                    _open.Add(handle);
                    var name = Marshal.PtrToStringAnsi(SDL_GameControllerName(handle));
                    Log($"SDL controller {i} opened: {name}");
                }
            }
        }
    }

    // ---- Windows: XInput ---------------------------------------------------

    private sealed class XInputPad : PadBase
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint PacketNumber;
            public short LX, LY, RX, RY;
            public ushort LeftTrigger, RightTrigger;
            public ushort Buttons;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(int index, ref XINPUT_STATE state);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const ushort BtnDpadUp = 0x0001, BtnDpadDown = 0x0002, BtnDpadLeft = 0x0004, BtnDpadRight = 0x0008;
        private const ushort BtnStart = 0x0010, BtnA = 0x1000, BtnB = 0x2000;
        private const short StickThreshold = 16000;

        public XInputPad(Action<PadButton> emit) : base(emit) { }

        public void Loop(ref bool stop)
        {
            while (!stop)
            {
                try
                {
                    var any = false;
                    for (var i = 0; i < 4 && !any; i++)
                    {
                        var s = new XINPUT_STATE();
                        if (XInputGetState(i, ref s) == 0)
                        {
                            if (!any) { any = true; Log($"XInput pad {i} connected"); }
                            var b = s.Buttons;
                            Frame(
                                up: (b & BtnDpadUp) != 0 || s.LY > StickThreshold,
                                down: (b & BtnDpadDown) != 0 || s.LY < -StickThreshold,
                                left: (b & BtnDpadLeft) != 0 || s.LX < -StickThreshold,
                                right: (b & BtnDpadRight) != 0 || s.LX > StickThreshold,
                                a: (b & BtnA) != 0,
                                b: (b & BtnB) != 0,
                                start: (b & BtnStart) != 0);
                        }
                    }
                    Thread.Sleep(16);
                }
                catch (OperationCanceledException) { break; }
                catch { Thread.Sleep(250); /* xinput missing: retry occasionally */ }
            }
        }
    }

    // ---- Linux: /dev/input/js0 joystick API --------------------------------

    private sealed class LinuxJsPad : PadBase
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct JsEvent
        {
            public uint Time;
            public short Value;
            public byte Type;
            public byte Number;
        }

        private const byte TypeButton = 0x01;
        private const byte TypeAxis = 0x02;

        public LinuxJsPad(Action<PadButton> emit) : base(emit) { }

        public void Loop(ref bool stop)
        {
            while (!stop)
            {
                var path = Enumerable.Range(0, 4)
                    .Select(i => $"/dev/input/js{i}")
                    .FirstOrDefault(File.Exists);
                if (path is null) { Thread.Sleep(2000); continue; }

                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8);
                    var buf = new byte[8];
                    var up = false; var down = false; var left = false; var right = false;
                    var a = false; var b = false; var start = false;

                    while (!stop)
                    {
                        var read = 0;
                        while (read < 8)
                        {
                            var n = fs.Read(buf, read, 8 - read);
                            if (n <= 0) return;
                            read += n;
                        }
                        var ev = MemoryMarshal.Read<JsEvent>(buf);
                        switch (ev.Type)
                        {
                            case TypeButton:
                                switch (ev.Number)
                                {
                                    case 0: a = ev.Value != 0; break;     // A / cross
                                    case 1: b = ev.Value != 0; break;     // B / circle
                                    case 9: start = ev.Value != 0; break; // start/options
                                }
                                break;
                            case TypeAxis:
                                switch (ev.Number)
                                {
                                    case 5: up = ev.Value < 0; down = ev.Value > 0; break;      // dpad Y
                                    case 4: left = ev.Value < 0; right = ev.Value > 0; break;   // dpad X
                                }
                                break;
                        }
                        Frame(up, down, left, right, a, b, start);
                    }
                }
                catch { Thread.Sleep(2000); }
            }
        }
    }
}
