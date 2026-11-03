using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GtTelemetry.Core.Azimuth;
using GtTelemetry.Core.Azimuth.Games.Fh2;
using Xunit;
using static GtTelemetry.Core.Azimuth.Games.Fh2.Fh2Layout;

// Plants a fake CCarDynamics in this process's own heap and finds it the
// same way the tool finds the real one in azimuth.exe.
public class ScannerTests
{
    [Fact]
    public void FindsPlantedCarInOwnProcess()
    {
        IntPtr mem = Marshal.AllocHGlobal(BlockSize + 16);
        try
        {
            ulong car = ((ulong)mem + 15) & ~15UL;
            var block = new byte[BlockSize];
            BinaryPrimitives.WriteUInt64LittleEndian(block, VtCarDynamics);
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(ICarDynamicsOffset), VtICarDynamics);
            BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(Abs(Icd.EngineRadS)), 314.159f);
            Marshal.Copy(block, 0, (IntPtr)car, block.Length);

            // A decoy with only the first vtable must not match.
            var decoy = new ulong[ICarDynamicsOffset / 8 + 1];
            decoy[0] = VtCarDynamics;
            GC.KeepAlive(decoy);

            using var g = GameProcess.Attach(Process.GetCurrentProcess())!;
            var found = CarScanner.Scan(g, Fh2Game.Instance);
            Assert.Contains(car, found);
            Assert.Single(found, a => a == car);

            var back = new byte[BlockSize];
            Assert.True(g.Read(car, back));
            Assert.True(Fh2Game.Instance.IsCar(back));
            var s = new CarSample();
            Fh2Reader.Fill(back, s);
            Assert.Equal(3000, s.Rpm, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }
}
