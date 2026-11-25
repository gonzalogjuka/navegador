using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Astro.Voz;

/// <summary>
/// Prioridad a la voz (fase 8.2): en una reunión, lo que hablás no espera a ninguna pestaña.
///   · El proceso entero corre con prioridad alta y sin ahorro de energía: Windows 11 les baja la velocidad a los
///     programas sin ventana ("modo eficiencia"), y este no tiene ventana.
///   · Los dos hilos de audio (el que recibe tu voz y el que traduce) se anotan en el planificador multimedia de Windows
///     (MMCSS, tarea "Pro Audio"), el mismo que usan los programas de audio para que no haya cortes.
/// Nada de esto necesita permisos de administrador. Si Windows lo rechaza, el traductor anda igual, con prioridad normal.
/// </summary>
internal readonly struct AudioPriority : IDisposable
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1, PowerThrottlingExecutionSpeed = 0x1;
    private const int AvrtPriorityHigh = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(nint handle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);

    private readonly nint _task;

    private AudioPriority(nint task) => _task = task;

    /// <summary>El hilo quedó anotado en MMCSS.</summary>
    public bool Active => _task != 0;

    /// <summary>Prioridad alta y sin ahorro de energía para todo el proceso. Devuelve si se pudo.</summary>
    public static bool ForThisProcess()
    {
        var ok = true;
        try
        {
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = ProcessPriorityClass.High;
            // ControlMask con la velocidad de ejecución y StateMask en 0 = "nunca me frenes para ahorrar energía".
            var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion, ControlMask = PowerThrottlingExecutionSpeed, StateMask = 0 };
            ok = SetProcessInformation(me.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or EntryPointNotFoundException)
        {
            ok = false;
        }
        return ok;
    }

    /// <summary>Anota el hilo actual en MMCSS ("Pro Audio"). Al soltarlo (Dispose, en el mismo hilo) vuelve a lo normal.</summary>
    public static AudioPriority ForThisThread()
    {
        try
        {
            uint index = 0;
            var task = AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
            if (task != 0) AvSetMmThreadPriority(task, AvrtPriorityHigh);
            return new AudioPriority(task);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        if (_task != 0) AvRevertMmThreadCharacteristics(_task);
    }
}
