using System.Runtime.InteropServices;

namespace GtTelemetry.App.Input;

// DirectInput's standard c_dfDIJoystick2 data format (DIJOYSTATE2), built
// by hand: it lives in dinput8.lib, not the DLL, and Vortice's own joystick
// format leaves out the velocity, acceleration and force blocks, where
// DirectInput puts a device's extra axes (Azimuth's slots 8-31). Using the
// same format as Azimuth gives the same slot numbers. Also the raw
// IDirectInputDevice8W calls Vortice doesn't wrap correctly.
static unsafe class JoystickFormat
{
    public const int StateSize = 0x110;   // sizeof(DIJOYSTATE2)
    public const int PovOffset = 0x20, ButtonOffset = 0x30, Buttons = 128, Povs = 4;

    const uint Optional = 0x80000000, AnyInstance = 0x00FFFF00, Axis = 0x3, Pov = 0x10, Button = 0xC;
    const uint Position = 0x100, Velocity = 0x200, Accel = 0x300, Force = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectFormat { public Guid* Guid; public uint Offset, Type, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DataFormat { public uint Size, ObjectSize, Flags, DataSize, Count; public ObjectFormat* Objects; }

    static readonly DataFormat* Format = Build();

    static DataFormat* Build()
    {
        // GUID_XAxis, YAxis, ZAxis, RxAxis, RyAxis, RzAxis, Slider, POV
        uint[] first = { 0xA36D02E0, 0xA36D02E1, 0xA36D02E2, 0xA36D02F4, 0xA36D02F5, 0xA36D02E3, 0xA36D02E4, 0xA36D02F2 };
        var guids = (Guid*)NativeMemory.Alloc((nuint)(first.Length * sizeof(Guid)));
        for (int i = 0; i < first.Length; i++)
            guids[i] = new Guid(first[i], 0xC9F3, 0x11CF, 0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00);

        var list = new List<ObjectFormat>();
        void Axes(uint at, uint aspect)
        {
            for (int a = 0; a < 6; a++) list.Add(new() { Guid = guids + a, Offset = at + (uint)a * 4, Type = Optional | Axis | AnyInstance, Flags = aspect });
            for (int s = 0; s < 2; s++) list.Add(new() { Guid = guids + 6, Offset = at + 24 + (uint)s * 4, Type = Optional | Axis | AnyInstance, Flags = aspect });
        }
        Axes(0x00, Position);
        for (int p = 0; p < Povs; p++) list.Add(new() { Guid = guids + 7, Offset = PovOffset + (uint)p * 4, Type = Optional | Pov | AnyInstance });
        for (int b = 0; b < Buttons; b++) list.Add(new() { Guid = null, Offset = ButtonOffset + (uint)b, Type = Optional | Button | AnyInstance });
        Axes(0xB0, Velocity);
        Axes(0xD0, Accel);
        Axes(0xF0, Force);

        var objects = (ObjectFormat*)NativeMemory.Alloc((nuint)(list.Count * sizeof(ObjectFormat)));
        for (int i = 0; i < list.Count; i++) objects[i] = list[i];
        var f = (DataFormat*)NativeMemory.Alloc((nuint)sizeof(DataFormat));
        *f = new DataFormat
        {
            Size = (uint)sizeof(DataFormat), ObjectSize = (uint)sizeof(ObjectFormat), Flags = 1 /* DIDF_ABSAXIS */,
            DataSize = StateSize, Count = (uint)list.Count, Objects = objects,
        };
        return f;   // kept for the life of the process
    }

    static IntPtr* Vtbl(IntPtr device) => *(IntPtr**)device;

    // IDirectInputDevice8W::SetDataFormat (vtable slot 11)
    public static int Set(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, DataFormat*, int>)Vtbl(device)[11])(device, Format);

    // IDirectInputDevice8W::GetDeviceState (slot 9)
    public static int Read(IntPtr device, byte[] state)
    {
        fixed (byte* p = state)
            return ((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Vtbl(device)[9])(device, StateSize, p);
    }

    // IDirectInputDevice8W::GetObjectInfo by data-format offset (slot 14): the
    // name of the object DirectInput put at that offset, or null.
    public static string? ObjectAt(IntPtr device, int offset)
    {
        const int size = 0x240;  // sizeof(DIDEVICEOBJECTINSTANCEW)
        byte* buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        *(int*)buf = size;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int, int>)Vtbl(device)[14])(device, buf, offset, 1 /* DIPH_BYOFFSET */);
        return hr < 0 ? null : new string((char*)(buf + 32)).Trim();
    }
}
