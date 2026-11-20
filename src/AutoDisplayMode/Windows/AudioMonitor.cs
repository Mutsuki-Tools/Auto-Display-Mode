using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AutoDisplayMode.Windows
{
    /// <summary>聲音偵測（Windows Core Audio 音量表，讀取所有播放裝置的峰值）。</summary>
    sealed class AudioMonitor : IDisposable
    {
        const int eRender = 0;
        const int DEVICE_STATE_ACTIVE = 1;
        const int CLSCTX_ALL = 23;

        IMMDeviceEnumerator? enumerator;
        readonly List<IAudioMeterInformation> meters = new();
        bool refreshRequested = true;
        int ticks;

        public AudioMonitor()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        }

        public void RequestRefresh() => refreshRequested = true;

        public float GetPeak()
        {
            // 每 30 秒重新列舉一次播放裝置（耳機插拔、螢幕切換會改變裝置）
            if (refreshRequested || ++ticks >= 30)
            {
                Refresh();
                ticks = 0;
                refreshRequested = false;
            }

            float max = 0f;
            foreach (var meter in meters)
            {
                if (meter.GetPeakValue(out float p) != 0)
                {
                    refreshRequested = true;
                    continue;
                }
                if (p > max) max = p;
            }
            return max;
        }

        void Refresh()
        {
            ReleaseMeters();
            if (enumerator == null) return;

            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection col) != 0 || col == null) return;

            try
            {
                if (col.GetCount(out uint count) != 0) return;

                for (uint i = 0; i < count; i++)
                {
                    if (col.Item(i, out IMMDevice dev) != 0 || dev == null) continue;
                    try
                    {
                        Guid iid = typeof(IAudioMeterInformation).GUID;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) == 0 && o != null)
                            meters.Add((IAudioMeterInformation)o);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(col);
            }
        }

        void ReleaseMeters()
        {
            foreach (var m in meters)
            {
                try { Marshal.ReleaseComObject(m); } catch { }
            }
            meters.Clear();
        }

        public void Dispose()
        {
            ReleaseMeters();
            if (enumerator != null)
            {
                Marshal.ReleaseComObject(enumerator);
                enumerator = null;
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, [Out, MarshalAs(UnmanagedType.LPArray)] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }
}
