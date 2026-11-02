using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SDL3;

namespace fire.Terminal.Sdl
{
    /// <summary>Window.StartDrag for the VM's SDL3 windows: text and files are dragged out of the window into other programs (SDL3 cannot start such a drag).
    /// The same as the native display (native/platform/std/fire_dragout.h): on Windows OLE's DoDragDrop with an IDataObject (CF_UNICODETEXT, CF_HDROP) and an
    /// IDropSource of this file (COM objects built by hand: tables of unmanaged function pointers), on X11 the source side of XDND with the connection of SDL
    /// (libX11 loaded at run time - a machine without X11 simply has no drag). Called on the thread that owns the window; returns when the user lets go:
    /// 0 nothing, 1 copied, 2 moved.</summary>
    public static class SdlDragOut
    {
        public static int Start(IntPtr window, string text, string[] files)
        {
            if (window == IntPtr.Zero || (string.IsNullOrEmpty(text) && files.Length == 0)) return 0;
            try
            {
                uint props = SDL.GetWindowProperties(window);
                if (OperatingSystem.IsWindows())
                {
                    IntPtr hwnd = SDL.GetPointerProperty(props, SDL.Props.WindowWin32HWNDPointer, IntPtr.Zero);
                    return hwnd == IntPtr.Zero ? 0 : OleDragSource.Run(text, files);
                }
                if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
                {
                    if (SDL.GetCurrentVideoDriver() != "x11") return 0;
                    IntPtr display = SDL.GetPointerProperty(props, SDL.Props.WindowX11DisplayPointer, IntPtr.Zero);
                    long xwindow = SDL.GetNumberProperty(props, SDL.Props.WindowX11WindowNumber, 0);
                    if (display == IntPtr.Zero || xwindow == 0) return 0;
                    // (nobody else takes the events of SDL's connection meanwhile: the pumping of the windows holds the same lock)
                    lock (SdlGate.Lock) return XdndSource.Run(display, (nuint)xwindow, text, files);
                }
            }
            catch (Exception) { }   // (no SDL, no libX11, no ole32: no drag)
            return 0;
        }

        /// <summary>The paths as a text/uri-list (file:// URIs, CR LF; what a URI may not hold as %XX).</summary>
        internal static string UriList(string[] files)
        {
            var sb = new StringBuilder();
            foreach (string path in files)
            {
                if (path.Length == 0) continue;
                sb.Append("file://");
                if (path.Length >= 2 && path[1] == ':') sb.Append('/');
                foreach (byte b in Encoding.UTF8.GetBytes(path.Replace('\\', '/')))
                {
                    char c = (char)b;
                    if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "/-_.~:!$&'()*+,;=@".IndexOf(c) >= 0)) sb.Append(c);
                    else sb.Append('%').Append(b.ToString("X2"));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------
    // X11: XDND as the source (see fire_dragout.h for the protocol and why the loop asks for the pointer instead of grabbing it)
    // ---------------------------------------------------------------------------------------------------------------------------------------------------
    internal static unsafe class XdndSource
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XClient { public int type; public nuint serial; public int send_event; public IntPtr display; public nuint window; public nuint message_type; public int format; public nint l0, l1, l2, l3, l4; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XSelRequest { public int type; public nuint serial; public int send_event; public IntPtr display; public nuint owner, requestor, selection, target, property, time; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XSelNotify { public int type; public nuint serial; public int send_event; public IntPtr display; public nuint requestor, selection, target, property, time; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XProperty { public int type; public nuint serial; public int send_event; public IntPtr display; public nuint window, atom, time; public int state; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XEvent { public fixed long pad[24]; }   // (Xlib's XEvent: 24 longs - on 64-bit Linux a long is 8 bytes; a 32-bit host would only reserve more)

        private const int PropertyNotify = 28, SelectionRequest = 30, SelectionNotify = 31, ClientMessage = 33;
        private const nuint XA_ATOM = 4, XA_WINDOW = 33;
        private const uint Buttons = 0x1Fu << 8, ShiftMask = 1;

        private static bool _tried;
        private static IntPtr _lib;
        private static delegate* unmanaged<IntPtr, byte*, int, nuint> InternAtom;
        private static delegate* unmanaged<IntPtr, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int> GetWindowProperty;
        private static delegate* unmanaged<void*, int> Free;
        private static delegate* unmanaged<IntPtr, nuint, int, nint, XEvent*, int> SendEvent;
        private static delegate* unmanaged<IntPtr, int> Flush;
        private static delegate* unmanaged<IntPtr, int, int> Sync;
        private static delegate* unmanaged<IntPtr, nuint, nuint, nuint, int, int, byte*, int, int> ChangeProperty;
        private static delegate* unmanaged<IntPtr, nuint, nuint, int> DeleteProperty;
        private static delegate* unmanaged<IntPtr, nuint, nuint, nuint, int> SetSelectionOwner;
        private static delegate* unmanaged<IntPtr, nuint, nuint*, nuint*, int*, int*, int*, int*, uint*, int> QueryPointer;
        private static delegate* unmanaged<IntPtr, nuint, nuint, int, int, int*, int*, nuint*, int> TranslateCoordinates;
        private static delegate* unmanaged<IntPtr, XEvent*, delegate* unmanaged<IntPtr, XEvent*, IntPtr, int>, IntPtr, int> CheckIfEvent;
        private static delegate* unmanaged<IntPtr, nuint> DefaultRootWindow;
        private static delegate* unmanaged<IntPtr, nuint, byte> KeysymToKeycode;
        private static delegate* unmanaged<IntPtr, byte*, int> QueryKeymap;
        private static delegate* unmanaged<IntPtr, IntPtr> SetErrorHandler;

        private static bool Load()
        {
            if (_tried) return _lib != IntPtr.Zero;
            _tried = true;
            if (!NativeLibrary.TryLoad("libX11.so.6", out _lib) && !NativeLibrary.TryLoad("libX11.so", out _lib)) return false;
            try
            {
                IntPtr F(string name) => NativeLibrary.GetExport(_lib, name);
                InternAtom = (delegate* unmanaged<IntPtr, byte*, int, nuint>)F("XInternAtom");
                GetWindowProperty = (delegate* unmanaged<IntPtr, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int>)F("XGetWindowProperty");
                Free = (delegate* unmanaged<void*, int>)F("XFree");
                SendEvent = (delegate* unmanaged<IntPtr, nuint, int, nint, XEvent*, int>)F("XSendEvent");
                Flush = (delegate* unmanaged<IntPtr, int>)F("XFlush");
                Sync = (delegate* unmanaged<IntPtr, int, int>)F("XSync");
                ChangeProperty = (delegate* unmanaged<IntPtr, nuint, nuint, nuint, int, int, byte*, int, int>)F("XChangeProperty");
                DeleteProperty = (delegate* unmanaged<IntPtr, nuint, nuint, int>)F("XDeleteProperty");
                SetSelectionOwner = (delegate* unmanaged<IntPtr, nuint, nuint, nuint, int>)F("XSetSelectionOwner");
                QueryPointer = (delegate* unmanaged<IntPtr, nuint, nuint*, nuint*, int*, int*, int*, int*, uint*, int>)F("XQueryPointer");
                TranslateCoordinates = (delegate* unmanaged<IntPtr, nuint, nuint, int, int, int*, int*, nuint*, int>)F("XTranslateCoordinates");
                CheckIfEvent = (delegate* unmanaged<IntPtr, XEvent*, delegate* unmanaged<IntPtr, XEvent*, IntPtr, int>, IntPtr, int>)F("XCheckIfEvent");
                DefaultRootWindow = (delegate* unmanaged<IntPtr, nuint>)F("XDefaultRootWindow");
                KeysymToKeycode = (delegate* unmanaged<IntPtr, nuint, byte>)F("XKeysymToKeycode");
                QueryKeymap = (delegate* unmanaged<IntPtr, byte*, int>)F("XQueryKeymap");
                SetErrorHandler = (delegate* unmanaged<IntPtr, IntPtr>)F("XSetErrorHandler");
                return true;
            }
            catch (Exception) { _lib = IntPtr.Zero; return false; }
        }

        // the state of the drag (one at a time: SdlGate.Lock); the predicate of XCheckIfEvent reads it
        private static IntPtr _dpy;
        private static nuint _src, _root;
        private static nuint _aSelection, _aAware, _aProxy, _aEnter, _aPosition, _aStatus, _aLeave, _aDrop, _aFinished, _aTypeList, _aCopy, _aMove, _aTargets, _aUriList, _aUtf8Mime, _aUtf8String, _aTextPlain;
        private static nuint[] _types = Array.Empty<nuint>();
        private static byte[]? _text, _uris;
        private static nuint _target, _targetProxy, _want, _action;
        private static int _version, _px, _py, _result;
        private static bool _waiting, _pendingPos, _accepted, _releasing, _dropped, _done;
        private static long _releaseAt, _dropAt, _ms0;
        private static nuint _time0;

        [UnmanagedCallersOnly]
        private static int Ours(IntPtr dpy, XEvent* e, IntPtr arg)
        {
            int type = *(int*)e;
            if (type == ClientMessage) { var c = (XClient*)e; return c->window == _src && (c->message_type == _aStatus || c->message_type == _aFinished) ? 1 : 0; }
            if (type == SelectionRequest) { var r = (XSelRequest*)e; return r->selection == _aSelection && r->owner == _src ? 1 : 0; }
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int Stamp(IntPtr dpy, XEvent* e, IntPtr arg)
        {
            var p = (XProperty*)e;
            return p->type == PropertyNotify && p->window == _src && p->atom == _aTypeList ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static int IgnoreError(IntPtr display, IntPtr error) => 0;

        private static long Now() => Environment.TickCount64;
        private static nuint Time() => _time0 == 0 ? 0 : _time0 + (nuint)(Now() - _ms0);

        private static nuint Atom(string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = bytes) return InternAtom(_dpy, p, 0);
        }

        private static nuint Prop32(nuint window, nuint prop, nuint type)
        {
            nuint actual = 0, n = 0, after = 0, value = 0;
            int format = 0;
            byte* data = null;
            if (GetWindowProperty(_dpy, window, prop, 0, 1, 0, type, &actual, &format, &n, &after, &data) == 0 && data != null && actual == type && format == 32 && n >= 1)
                value = *(nuint*)data;
            if (data != null) Free(data);
            return value;
        }

        private static nuint TargetAt(int x, int y, out int version)
        {
            version = 0;
            nuint w = _root, child = 0;
            int tx, ty;
            for (int depth = 0; depth < 16; depth++)
            {
                if (TranslateCoordinates(_dpy, _root, w, x, y, &tx, &ty, &child) == 0 || child == 0) return 0;
                w = child;
                if (w == _src) return 0;
                nuint aware = Prop32(w, _aAware, XA_ATOM);
                if (aware != 0)
                {
                    if (aware < 3) return 0;
                    version = aware > 5 ? 5 : (int)aware;
                    return w;
                }
            }
            return 0;
        }

        private static void Send(nuint message, nint l1, nint l2, nint l3, nint l4)
        {
            XEvent e = default;
            var c = (XClient*)&e;
            c->type = ClientMessage;
            c->display = _dpy;
            c->window = _target;
            c->message_type = message;
            c->format = 32;
            c->l0 = (nint)_src; c->l1 = l1; c->l2 = l2; c->l3 = l3; c->l4 = l4;
            SendEvent(_dpy, _targetProxy != 0 ? _targetProxy : _target, 0, 0, &e);
        }

        private static void Position(int x, int y)
        {
            Send(_aPosition, 0, ((nint)(x & 0xFFFF) << 16) | (nint)(y & 0xFFFF), (nint)Time(), (nint)_want);
            _waiting = true;
            _pendingPos = false;
        }

        private static void Move(int x, int y, uint mask)
        {
            nuint t = TargetAt(x, y, out int version);
            _want = (mask & ShiftMask) != 0 ? _aMove : _aCopy;
            if (t != _target)
            {
                if (_target != 0) Send(_aLeave, 0, 0, 0, 0);
                _target = t;
                _version = version;
                _waiting = _pendingPos = _accepted = false;
                _action = 0;
                _targetProxy = 0;
                if (t != 0)
                {
                    nuint proxy = Prop32(t, _aProxy, XA_WINDOW);
                    if (proxy != 0 && Prop32(proxy, _aProxy, XA_WINDOW) == proxy) _targetProxy = proxy;
                    Send(_aEnter, ((nint)_version << 24) | (_types.Length > 3 ? 1 : 0), (nint)_types[0], _types.Length > 1 ? (nint)_types[1] : 0, _types.Length > 2 ? (nint)_types[2] : 0);
                }
            }
            if (_target == 0) return;
            if (_waiting) { _pendingPos = true; _px = x; _py = y; return; }
            Position(x, y);
        }

        private static void Decide()
        {
            _releasing = false;
            if (_target == 0) { _done = true; return; }
            if (_accepted)
            {
                Send(_aDrop, 0, (nint)Time(), 0, 0);
                _dropped = true;
                _dropAt = Now();
            }
            else
            {
                Send(_aLeave, 0, 0, 0, 0);
                _done = true;
            }
        }

        private static int Effect(nuint action) => action == _aMove ? 2 : 1;

        private static void Answer(XSelRequest* r)
        {
            nuint prop = r->property != 0 ? r->property : r->target;
            bool ok = false;
            if (r->target == _aTargets)
            {
                var atoms = new nuint[_types.Length + 1];
                atoms[0] = _aTargets;
                Array.Copy(_types, 0, atoms, 1, _types.Length);
                fixed (nuint* p = atoms) ChangeProperty(_dpy, r->requestor, prop, XA_ATOM, 32, 0, (byte*)p, atoms.Length);
                ok = true;
            }
            else
            {
                byte[]? data = r->target == _aUriList ? _uris : (r->target == _aUtf8Mime || r->target == _aUtf8String || r->target == _aTextPlain) ? _text : null;
                if (data != null && data.Length > 0)
                {
                    fixed (byte* p = data) ChangeProperty(_dpy, r->requestor, prop, r->target, 8, 0, p, data.Length);
                    ok = true;
                }
            }
            XEvent reply = default;
            var n = (XSelNotify*)&reply;
            n->type = SelectionNotify;
            n->display = _dpy;
            n->requestor = r->requestor;
            n->selection = r->selection;
            n->target = r->target;
            n->property = ok ? prop : 0;
            n->time = r->time;
            SendEvent(_dpy, r->requestor, 0, 0, &reply);
        }

        private static void Handle(XEvent* e)
        {
            int type = *(int*)e;
            if (type == SelectionRequest) { Answer((XSelRequest*)e); return; }
            var c = (XClient*)e;
            if (type != ClientMessage || _target == 0 || (nuint)c->l0 != _target) return;
            if (c->message_type == _aStatus && !_dropped)
            {
                _waiting = false;
                _accepted = (c->l1 & 1) != 0;
                _action = _accepted ? ((nuint)c->l4 != 0 ? (nuint)c->l4 : _aCopy) : 0;
                if (_pendingPos) Position(_px, _py);
            }
            else if (c->message_type == _aFinished && _dropped)
            {
                bool ok = _version < 5 || (c->l1 & 1) != 0;
                nuint done = _version >= 5 && (nuint)c->l2 != 0 ? (nuint)c->l2 : _action;
                _result = ok ? Effect(done) : 0;
                _done = true;
            }
        }

        public static int Run(IntPtr display, nuint window, string text, string[] files)
        {
            if (!Load()) return 0;
            _dpy = display;
            _src = window;
            _root = DefaultRootWindow(display);
            nuint rootRet, childRet;
            int rx, ry, wx, wy;
            uint mask;
            if (QueryPointer(display, _root, &rootRet, &childRet, &rx, &ry, &wx, &wy, &mask) == 0 || (mask & Buttons) == 0) return 0;
            _aSelection = Atom("XdndSelection"); _aAware = Atom("XdndAware"); _aProxy = Atom("XdndProxy"); _aEnter = Atom("XdndEnter");
            _aPosition = Atom("XdndPosition"); _aStatus = Atom("XdndStatus"); _aLeave = Atom("XdndLeave"); _aDrop = Atom("XdndDrop");
            _aFinished = Atom("XdndFinished"); _aTypeList = Atom("XdndTypeList"); _aCopy = Atom("XdndActionCopy"); _aMove = Atom("XdndActionMove");
            _aTargets = Atom("TARGETS"); _aUriList = Atom("text/uri-list"); _aUtf8Mime = Atom("text/plain;charset=utf-8"); _aUtf8String = Atom("UTF8_STRING");
            _aTextPlain = Atom("text/plain");
            var types = new List<nuint>();
            _uris = files.Length > 0 ? Encoding.UTF8.GetBytes(SdlDragOut.UriList(files)) : null;
            if (_uris != null && _uris.Length > 0) types.Add(_aUriList);
            _text = string.IsNullOrEmpty(text) ? null : Encoding.UTF8.GetBytes(text);
            if (_text != null) { types.Add(_aUtf8Mime); types.Add(_aUtf8String); types.Add(_aTextPlain); }
            if (types.Count == 0) return 0;
            _types = types.ToArray();
            _target = _targetProxy = _want = _action = 0;
            _version = _result = 0;
            _waiting = _pendingPos = _accepted = _releasing = _dropped = _done = false;
            _time0 = 0;
            byte escape = KeysymToKeycode(display, 0xFF1B);
            IntPtr oldHandler = SetErrorHandler((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&IgnoreError);
            XEvent ev;
            try
            {
                // the kinds (all of them, for a target that wants more than three) - the change of the property gives the time of the server
                fixed (nuint* p = _types) ChangeProperty(display, _src, _aTypeList, XA_ATOM, 32, 0, (byte*)p, _types.Length);
                Flush(display);
                long start = Now();
                while (Now() - start < 200)
                {
                    if (CheckIfEvent(display, &ev, &Stamp, IntPtr.Zero) != 0) { _time0 = ((XProperty*)&ev)->time; break; }
                    Thread.Sleep(5);
                }
                _ms0 = Now();
                SetSelectionOwner(display, _aSelection, _src, Time());
                int lastX = rx, lastY = ry;
                Move(rx, ry, mask);
                Flush(display);
                byte* keys = stackalloc byte[32];
                while (!_done)
                {
                    while (!_done && CheckIfEvent(display, &ev, &Ours, IntPtr.Zero) != 0) Handle(&ev);
                    if (_done) break;
                    long now = Now();
                    if (!_dropped && !_releasing)
                    {
                        if (QueryPointer(display, _root, &rootRet, &childRet, &rx, &ry, &wx, &wy, &mask) == 0) mask = 0;
                        for (int i = 0; i < 32; i++) keys[i] = 0;
                        QueryKeymap(display, keys);
                        if (escape != 0 && (keys[escape / 8] & (1 << (escape % 8))) != 0)
                        {
                            if (_target != 0) Send(_aLeave, 0, 0, 0, 0);
                            _result = 0;
                            break;
                        }
                        if (rx != lastX || ry != lastY) { lastX = rx; lastY = ry; Move(rx, ry, mask); }
                        if ((mask & Buttons) == 0) { _releasing = true; _releaseAt = now; }
                    }
                    if (_releasing && ((!_waiting && !_pendingPos) || now - _releaseAt > 500)) Decide();
                    if (_dropped && !_done && now - _dropAt > 5000) { _result = _accepted ? Effect(_action) : 0; break; }
                    if (_done) break;
                    Flush(display);
                    Thread.Sleep(10);
                }
            }
            finally
            {
                SetSelectionOwner(display, _aSelection, 0, Time());
                DeleteProperty(display, _src, _aTypeList);
                Sync(display, 0);
                SetErrorHandler(oldHandler);
                _text = _uris = null;
            }
            return _result;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------
    // Windows: OLE's DoDragDrop with an IDataObject and an IDropSource of our own (the objects are blocks of unmanaged memory: a pointer to the table of
    // functions, the count of references and the number of the drag whose data they give)
    // ---------------------------------------------------------------------------------------------------------------------------------------------------
    internal static unsafe class OleDragSource
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FormatEtc { public ushort cfFormat; public IntPtr ptd; public uint dwAspect; public int lindex; public uint tymed; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StgMedium { public uint tymed; public IntPtr hGlobal; public IntPtr pUnkForRelease; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Obj { public void** vtbl; public int refs; public int drag; public uint pos; }

        private const int S_OK = 0, S_FALSE = 1, E_NOINTERFACE = unchecked((int)0x80004002), E_NOTIMPL = unchecked((int)0x80004001), E_INVALIDARG = unchecked((int)0x80070057);
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E), DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_TYMED = unchecked((int)0x80040069);
        private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        private const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
        private const ushort CF_UNICODETEXT = 13, CF_HDROP = 15;
        private const uint TYMED_HGLOBAL = 1, MK_BUTTONS = 0x0001 | 0x0002 | 0x0010;

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDropSource = new("00000121-0000-0000-C000-000000000046");
        private static readonly Guid IID_IEnumFORMATETC = new("00000103-0000-0000-C000-000000000046");

        [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")] private static extern void OleUninitialize();
        [DllImport("ole32.dll")] private static extern int DoDragDrop(IntPtr dataObject, IntPtr dropSource, uint okEffects, out uint effect);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern int GlobalUnlock(IntPtr h);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        // the data of the drags that are going on (a target may hold the object after the drag: it gives its data as long as it lives)
        private static readonly Dictionary<int, (string? Text, string[] Files)> Drags = new();
        private static int _nextDrag;
        private static void** _dataVtbl, _sourceVtbl, _enumVtbl;

        private static void Tables()
        {
            if (_dataVtbl != null) return;
            _dataVtbl = (void**)NativeMemory.Alloc(12, (nuint)sizeof(void*));
            _dataVtbl[0] = (delegate* unmanaged[Stdcall]<Obj*, Guid*, void**, int>)&DataQuery;
            _dataVtbl[1] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
            _dataVtbl[2] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
            _dataVtbl[3] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, StgMedium*, int>)&GetData;
            _dataVtbl[4] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, StgMedium*, int>)&GetDataHere;
            _dataVtbl[5] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, int>)&QueryGetData;
            _dataVtbl[6] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, FormatEtc*, int>)&Canonical;
            _dataVtbl[7] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, StgMedium*, int, int>)&SetData;
            _dataVtbl[8] = (delegate* unmanaged[Stdcall]<Obj*, uint, Obj**, int>)&EnumFormats;
            _dataVtbl[9] = (delegate* unmanaged[Stdcall]<Obj*, FormatEtc*, uint, IntPtr, uint*, int>)&DAdvise;
            _dataVtbl[10] = (delegate* unmanaged[Stdcall]<Obj*, uint, int>)&DUnadvise;
            _dataVtbl[11] = (delegate* unmanaged[Stdcall]<Obj*, IntPtr*, int>)&EnumDAdvise;
            _sourceVtbl = (void**)NativeMemory.Alloc(5, (nuint)sizeof(void*));
            _sourceVtbl[0] = (delegate* unmanaged[Stdcall]<Obj*, Guid*, void**, int>)&SourceQuery;
            _sourceVtbl[1] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
            _sourceVtbl[2] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
            _sourceVtbl[3] = (delegate* unmanaged[Stdcall]<Obj*, int, uint, int>)&QueryContinueDrag;
            _sourceVtbl[4] = (delegate* unmanaged[Stdcall]<Obj*, uint, int>)&GiveFeedback;
            _enumVtbl = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
            _enumVtbl[0] = (delegate* unmanaged[Stdcall]<Obj*, Guid*, void**, int>)&EnumQuery;
            _enumVtbl[1] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
            _enumVtbl[2] = (delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
            _enumVtbl[3] = (delegate* unmanaged[Stdcall]<Obj*, uint, FormatEtc*, uint*, int>)&EnumNext;
            _enumVtbl[4] = (delegate* unmanaged[Stdcall]<Obj*, uint, int>)&EnumSkip;
            _enumVtbl[5] = (delegate* unmanaged[Stdcall]<Obj*, int>)&EnumReset;
            _enumVtbl[6] = (delegate* unmanaged[Stdcall]<Obj*, Obj**, int>)&EnumClone;
        }

        private static Obj* New(void** vtbl, int drag, uint pos = 0)
        {
            var o = (Obj*)NativeMemory.AllocZeroed((nuint)sizeof(Obj));
            o->vtbl = vtbl; o->refs = 1; o->drag = drag; o->pos = pos;
            Interlocked.Increment(ref Holders(drag));   // (the data of the drag lives as long as an object of it does)
            return o;
        }

        private static readonly Dictionary<int, int[]> HolderCounts = new();
        private static ref int Holders(int drag)
        {
            lock (HolderCounts)
            {
                if (!HolderCounts.TryGetValue(drag, out var c)) HolderCounts[drag] = c = new int[1];
                return ref c[0];
            }
        }

        private static List<ushort> Formats(int drag)
        {
            var list = new List<ushort>();
            lock (Drags)
                if (Drags.TryGetValue(drag, out var d))
                {
                    if (!string.IsNullOrEmpty(d.Text)) list.Add(CF_UNICODETEXT);
                    if (d.Files.Length > 0) list.Add(CF_HDROP);
                }
            return list;
        }

        private static int Query(Obj* self, Guid* iid, void** out_, Guid own)
        {
            if (out_ == null) return E_INVALIDARG;
            *out_ = null;
            if (iid == null || (*iid != IID_IUnknown && *iid != own)) return E_NOINTERFACE;
            Interlocked.Increment(ref self->refs);
            *out_ = self;
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int DataQuery(Obj* self, Guid* iid, void** out_) => Query(self, iid, out_, IID_IDataObject);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int SourceQuery(Obj* self, Guid* iid, void** out_) => Query(self, iid, out_, IID_IDropSource);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumQuery(Obj* self, Guid* iid, void** out_) => Query(self, iid, out_, IID_IEnumFORMATETC);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint AddRef(Obj* self) => (uint)Interlocked.Increment(ref self->refs);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint Release(Obj* self)
        {
            int left = Interlocked.Decrement(ref self->refs);
            if (left == 0)
            {
                int drag = self->drag;
                NativeMemory.Free(self);
                if (Interlocked.Decrement(ref Holders(drag)) == 0)
                {
                    lock (Drags) Drags.Remove(drag);
                    lock (HolderCounts) HolderCounts.Remove(drag);
                }
            }
            return (uint)left;
        }

        private static bool Offers(Obj* self, FormatEtc* f) =>
            f != null && (f->tymed & TYMED_HGLOBAL) != 0 && (f->dwAspect == 0 || f->dwAspect == 1) && Formats(self->drag).Contains(f->cfFormat);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int GetData(Obj* self, FormatEtc* f, StgMedium* m)
        {
            if (f == null || m == null) return E_INVALIDARG;
            if ((f->tymed & TYMED_HGLOBAL) == 0) return DV_E_TYMED;
            if (!Offers(self, f)) return DV_E_FORMATETC;
            (string? Text, string[] Files) d;
            lock (Drags) d = Drags[self->drag];
            byte[] bytes;
            if (f->cfFormat == CF_UNICODETEXT) bytes = Encoding.Unicode.GetBytes(d.Text + "\0");
            else
            {
                // DROPFILES (pFiles 20, pt, fNC, fWide 1), then the paths, each ending with a 0, and another 0
                byte[] list = Encoding.Unicode.GetBytes(string.Concat(d.Files.Select(p => p + "\0")) + "\0");
                bytes = new byte[20 + list.Length];
                BitConverter.GetBytes(20).CopyTo(bytes, 0);
                BitConverter.GetBytes(1).CopyTo(bytes, 16);
                list.CopyTo(bytes, 20);
            }
            IntPtr h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (nuint)bytes.Length);
            if (h == IntPtr.Zero) return E_OUTOFMEMORY;
            IntPtr p2 = GlobalLock(h);
            Marshal.Copy(bytes, 0, p2, bytes.Length);
            GlobalUnlock(h);
            m->tymed = TYMED_HGLOBAL;
            m->hGlobal = h;
            m->pUnkForRelease = IntPtr.Zero;
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int GetDataHere(Obj* self, FormatEtc* f, StgMedium* m) => E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryGetData(Obj* self, FormatEtc* f) => Offers(self, f) ? S_OK : DV_E_FORMATETC;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int Canonical(Obj* self, FormatEtc* inp, FormatEtc* outp) { if (outp != null) outp->ptd = IntPtr.Zero; return E_NOTIMPL; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int SetData(Obj* self, FormatEtc* f, StgMedium* m, int release) => E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumFormats(Obj* self, uint direction, Obj** out_)
        {
            if (out_ == null) return E_INVALIDARG;
            *out_ = null;
            if (direction != 1) return E_NOTIMPL;
            *out_ = New(_enumVtbl, self->drag);
            return S_OK;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int DAdvise(Obj* self, FormatEtc* f, uint flags, IntPtr sink, uint* connection) => OLE_E_ADVISENOTSUPPORTED;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int DUnadvise(Obj* self, uint connection) => OLE_E_ADVISENOTSUPPORTED;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumDAdvise(Obj* self, IntPtr* out_) { if (out_ != null) *out_ = IntPtr.Zero; return OLE_E_ADVISENOTSUPPORTED; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumNext(Obj* self, uint wanted, FormatEtc* outp, uint* fetched)
        {
            if (outp == null || (wanted > 1 && fetched == null)) return E_INVALIDARG;
            var formats = Formats(self->drag);
            uint n = 0;
            while (n < wanted && self->pos < formats.Count)
            {
                outp[n] = new FormatEtc { cfFormat = formats[(int)self->pos++], ptd = IntPtr.Zero, dwAspect = 1, lindex = -1, tymed = TYMED_HGLOBAL };
                n++;
            }
            if (fetched != null) *fetched = n;
            return n == wanted ? S_OK : S_FALSE;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumSkip(Obj* self, uint n)
        {
            int count = Formats(self->drag).Count;
            self->pos = (uint)Math.Min(count, self->pos + n);
            return self->pos < count ? S_OK : S_FALSE;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumReset(Obj* self) { self->pos = 0; return S_OK; }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int EnumClone(Obj* self, Obj** out_)
        {
            if (out_ == null) return E_INVALIDARG;
            *out_ = New(_enumVtbl, self->drag, self->pos);
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryContinueDrag(Obj* self, int escape, uint keys) => escape != 0 ? DRAGDROP_S_CANCEL : (keys & MK_BUTTONS) == 0 ? DRAGDROP_S_DROP : S_OK;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int GiveFeedback(Obj* self, uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;

        /// <summary>The drag on this thread (OLE needs a single-threaded apartment: a thread of .NET that is in the multi-threaded one runs it on a helper thread of its own).</summary>
        public static int Run(string text, string[] files)
        {
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0 && (GetAsyncKeyState(0x02) & 0x8000) == 0 && (GetAsyncKeyState(0x04) & 0x8000) == 0) return 0;
            int hr = OleInitialize(IntPtr.Zero);
            if (hr == RPC_E_CHANGED_MODE)
            {
                int result = 0;
                var helper = new Thread(() => result = Run(text, files)) { IsBackground = true, Name = "fire drag" };
                if (OperatingSystem.IsWindows()) helper.SetApartmentState(ApartmentState.STA);
                helper.Start();
                helper.Join();
                return result;
            }
            if (hr < 0) return 0;
            try
            {
                Tables();
                int drag;
                lock (Drags) { drag = ++_nextDrag; Drags[drag] = (text, files); }
                Obj* data = New(_dataVtbl, drag);
                Obj* source = New(_sourceVtbl, drag);
                hr = DoDragDrop((IntPtr)data, (IntPtr)source, 1 | 2, out uint effect);
                ((delegate* unmanaged[Stdcall]<Obj*, uint>)_dataVtbl[2])(data);
                ((delegate* unmanaged[Stdcall]<Obj*, uint>)_sourceVtbl[2])(source);
                if (hr != DRAGDROP_S_DROP) return 0;
                return (effect & 2) != 0 ? 2 : (effect & (1 | 4)) != 0 ? 1 : 0;
            }
            finally { OleUninitialize(); }
        }
    }
}
