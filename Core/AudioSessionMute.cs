using System.Runtime.InteropServices;

namespace DeskTwin.Core;

/// <summary>
/// 直接把本进程的音频会话静音（Windows Core Audio / WASAPI），
/// 不碰 RDP 连接参数，所以切换静音不需要重连。
/// RDP 控件在自己进程内渲染远程音频，因此按进程 ID 匹配即可。
/// </summary>
internal static class AudioSessionMute
{
    private const int ClsCtxAll = 23;

    /// <summary>设置本进程音频会话的静音状态；找不到会话（还没播放过声音）时返回 false。</summary>
    internal static bool TrySetMute(bool muted, out string? error)
    {
        error = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device) != 0
                || device is null)
            {
                error = "没有可用的播放设备。";
                return false;
            }

            var managerId = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out managerObject) != 0
                || managerObject is not IAudioSessionManager2 manager)
            {
                error = "无法获取音频会话管理器。";
                return false;
            }

            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
            {
                error = "无法枚举音频会话。";
                return false;
            }

            sessions.GetCount(out var count);
            var processId = Environment.ProcessId;
            var applied = false;
            for (var index = 0; index < count; index++)
            {
                if (sessions.GetSession(index, out var control) != 0 || control is null)
                {
                    continue;
                }

                try
                {
                    control.GetProcessId(out var sessionProcessId);
                    if (sessionProcessId != processId || control is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    var context = Guid.Empty;
                    if (volume.SetMute(muted, ref context) == 0)
                    {
                        applied = true;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }

            if (!applied)
            {
                error = "本进程还没有音频会话。";
            }

            return applied;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
        finally
        {
            Release(sessions);
            Release(managerObject);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private enum DataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl0();

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice? device);

        int NotImpl1();
        int NotImpl2();
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl0();
        int NotImpl1();

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);

        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl2? session);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl 的 9 个方法，这里只占位（不调用）
        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
        int NotImpl6();
        int NotImpl7();
        int NotImpl8();

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

        [PreserveSig]
        int GetProcessId(out int processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
