using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>The waiting functions of the devices package (`WaitFor`, `WaitForString`) as the VM runs them: the VM waits - abortable by `leave`/`terminate`, with the program's `#timeout` as the
    /// default - and asks the library, which looks into the receive buffer of the device, once per step (`__DEVWaitStep`). A native build has the same functions in C++ (waitFor).</summary>
    internal static class DeviceHostNatives
    {
        /// <summary>1: found (the buffer is cut behind it), 0: time up / device disconnected / program ended, -1: not a time.</summary>
        private static Value Wait(Value handle, byte[] pattern, Value timeout)
        {
            var needle = Value.MakeBuffer(new ByteBuffer(pattern, ByteConversions.HostByteOrder));
            long state = 0;
            try
            {
                VM.WaitUntil(() =>
                {
                    state = PackageNativeBinding.Call("__DEVWaitStep", handle, needle).AsInt();
                    return state != 0;   // found, or the device is gone
                }, timeout);
            }
            catch (ArgumentException) { return Value.MakeInt(-1); }
            return Value.MakeInt(state == 1 ? 1 : 0);
        }

        // one character per byte (Latin1), like ReadString/WriteString
        public static Value WaitForString(Value[] a) => Wait(a[0], System.Text.Encoding.Latin1.GetBytes(a[1].AsString()), a[2]);

        public static Value WaitFor(Value[] a) => Wait(a[0], (byte[])a[1].AsBuffer().Bytes.Clone(), a[2]);
    }
}
