//
// Copyright 2017-2023 Valve Corporation.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// HoF2VR: the minimal subset of Steam Audio 4.8.1's C# bindings (unity/src/project/SteamAudioUnity/Assets/Plugins/
// SteamAudio/Scripts/Runtime/SteamAudio.cs, tag v4.8.1) that the HRTF spatializer needs (VR/Hrtf.cs,
// docs/hrtf-plan.md): the context, the default HRTF and the Unity plugin's setup calls. Declarations copied verbatim
// (namespace and class names changed; iplUnityGetVersion declared from steamaudio_unity_native.h). LICENSE.md and
// THIRDPARTY.md next to this file.

using System;
using System.Runtime.InteropServices;

namespace HoF2VR.SteamAudio
{
    internal static class Constants
    {
        public const uint kVersionMajor = 4;
        public const uint kVersionMinor = 8;
        public const uint kVersionPatch = 1;
        public const uint kVersion = (kVersionMajor << 16) | (kVersionMinor << 8) | kVersionPatch;
    }

    internal enum Error
    {
        Success,
        Failure,
        OutOfMemory,
        Initialization
    }

    internal enum LogLevel
    {
        Info,
        Warning,
        Error,
        Debug
    }

    internal enum SIMDLevel
    {
        SSE2,
        SSE4,
        AVX,
        AVX2,
        AVX512,
        NEON = SSE2
    }

    [Flags]
    internal enum ContextFlags
    {
        Validation = 1 << 0,
        Force32Bit = 0x7fffffff
    }

    internal enum HRTFType
    {
        Default,
        SOFA
    }

    internal enum HRTFNormType
    {
        None,
        RMS
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void LogCallback(LogLevel level, string message);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr AllocateCallback(UIntPtr size, UIntPtr alignment);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void FreeCallback(IntPtr memoryBlock);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ContextSettings
    {
        public uint version;
        public LogCallback logCallback;
        public AllocateCallback allocateCallback;
        public FreeCallback freeCallback;
        public SIMDLevel simdLevel;
        public ContextFlags flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioSettings
    {
        public int samplingRate;
        public int frameSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HRTFSettings
    {
        public HRTFType type;
        public string sofaFileName;
        public IntPtr sofaFileData;
        public int sofaFileDataSize;
        public float volume;
        public HRTFNormType normType;
    }

    internal static class API
    {
        // Context (libphonon.so)

        [DllImport("phonon")]
        public static extern Error iplContextCreate(ref ContextSettings settings, out IntPtr context);

        [DllImport("phonon")]
        public static extern void iplContextRelease(ref IntPtr context);

        // HRTF (libphonon.so)

        [DllImport("phonon")]
        public static extern Error iplHRTFCreate(IntPtr context, ref AudioSettings audioSettings, ref HRTFSettings hrtfSettings, out IntPtr hrtf);

        [DllImport("phonon")]
        public static extern void iplHRTFRelease(ref IntPtr hrtf);

        // The Unity plugin (libaudioplugin_phonon.so)

        [DllImport("audioplugin_phonon")]
        public static extern void iplUnityGetVersion(out uint major, out uint minor, out uint patch);

        [DllImport("audioplugin_phonon")]
        public static extern void iplUnityInitialize(IntPtr context);

        [DllImport("audioplugin_phonon")]
        public static extern void iplUnitySetHRTF(IntPtr hrtf);

        [DllImport("audioplugin_phonon")]
        public static extern void iplUnitySetHRTFDisabled(bool disabled);
    }
}
