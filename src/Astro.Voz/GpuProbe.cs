using System.Runtime.InteropServices;

namespace Astro.Voz;

/// <summary>
/// ¿Hay una placa de video dedicada? Se le pregunta a Vulkan directamente (vulkan-1.dll, que instalan los drivers).
/// Sólo ahí conviene usar la GPU: su memoria es propia. Una integrada usa la memoria de la PC.
/// </summary>
internal static unsafe class GpuProbe
{
    private const int InstanceCreateInfoType = 1, Success = 0, Incomplete = 5;
    private const int DeviceTypeOffset = 16, DiscreteGpu = 2, Cpu = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int Type;
        public nint Next;
        public uint Flags;
        public nint ApplicationInfo;
        public uint LayerCount;
        public nint Layers;
        public uint ExtensionCount;
        public nint Extensions;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(in InstanceCreateInfo info, nint allocator, out nint instance);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(nint instance, ref uint count, nint* devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(nint device, byte* properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(nint instance, nint allocator);

    /// <summary>
    /// Posición de la primera placa dedicada entre las que ve Vulkan (sin contar las que son puro procesador), o -1 si
    /// no hay ninguna o la PC no tiene Vulkan.
    /// </summary>
    public static int DiscreteGpuIndex()
    {
        try
        {
            var info = new InstanceCreateInfo { Type = InstanceCreateInfoType };
            if (vkCreateInstance(in info, 0, out var instance) != Success) return -1;
            try
            {
                uint count = 16;
                var devices = stackalloc nint[16];
                var result = vkEnumeratePhysicalDevices(instance, ref count, devices);
                if (result != Success && result != Incomplete) return -1;
                var properties = stackalloc byte[2048]; // VkPhysicalDeviceProperties ocupa 824 bytes
                var index = 0;
                for (var i = 0; i < count; i++)
                {
                    vkGetPhysicalDeviceProperties(devices[i], properties);
                    var type = *(int*)(properties + DeviceTypeOffset);
                    if (type == DiscreteGpu) return index;
                    if (type != Cpu) index++;
                }
                return -1;
            }
            finally
            {
                vkDestroyInstance(instance, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return -1;
        }
    }
}
