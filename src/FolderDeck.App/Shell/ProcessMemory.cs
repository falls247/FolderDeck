using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

namespace FolderDeck.Shell;

/// <summary>
/// What the process's memory is made of, for the memory line of app.log
/// (<see cref="Services.AppServices.DescribeMemory"/>). The working set alone cannot tell a leak
/// from pages that Windows will trim (a log showed 950 MB falling to 150 MB with nothing closed):
/// this adds the private bytes (commit charge), the committed address space by kind — image (DLLs
/// and the exe), mapped (file mappings, shared sections: also GPU memory shared with the CPU),
/// private (native heaps, the GC heap, thread stacks) — the GC's own commit, and the handle, GDI,
/// USER and thread counts. A leak grows the private part; a big image part points at loaded
/// extensions, a big mapped part at file mappings or the GPU driver.
/// Any thread; a walk of the address space (a few thousand regions, about a millisecond). Never throws.
/// </summary>
internal static class ProcessMemory
{
    private const long Mb = 1024 * 1024;

    public static unsafe string Describe()
    {
        try
        {
            long image = 0, mapped = 0, privateBytes = 0;
            nuint address = 0;
            MEMORY_BASIC_INFORMATION info;
            nuint size = (nuint)sizeof(MEMORY_BASIC_INFORMATION);
            while (PInvoke.VirtualQuery((void*)address, &info, size) != 0)
            {
                if (info.State == VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT)
                {
                    long bytes = (long)info.RegionSize;
                    switch (info.Type)
                    {
                        case PAGE_TYPE.MEM_IMAGE:
                            image += bytes;
                            break;
                        case PAGE_TYPE.MEM_MAPPED:
                            mapped += bytes;
                            break;
                        case PAGE_TYPE.MEM_PRIVATE:
                            privateBytes += bytes;
                            break;
                    }
                }

                nuint next = (nuint)info.BaseAddress + info.RegionSize;
                if (next <= address)
                {
                    break;
                }

                address = next;
            }

            using var self = Process.GetCurrentProcess();
            var current = new HANDLE(self.Handle);
            uint gdi = PInvoke.GetGuiResources(current, GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS);
            uint user = PInvoke.GetGuiResources(current, GET_GUI_RESOURCES_FLAGS.GR_USEROBJECTS);
            long gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            return $"private {self.PrivateMemorySize64 / Mb} MB, committed image {image / Mb} / mapped {mapped / Mb} / private {privateBytes / Mb} MB, "
                + $"gc committed {gcCommitted / Mb} MB, handles {self.HandleCount}, gdi {gdi}, user {user}, threads {self.Threads.Count}";
        }
        catch (Exception ex)
        {
            return $"breakdown unavailable ({ex.GetType().Name})";
        }
    }
}
