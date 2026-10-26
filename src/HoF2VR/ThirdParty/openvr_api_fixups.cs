// Valve's openvr_api.cs (v1.26.7) references vrshared_double without defining it (upstream
// generator bug). In openvr.h it is a struct wrapping one double.
using System.Runtime.InteropServices;

namespace Valve.VR
{
    [StructLayout(LayoutKind.Sequential)]
    public struct vrshared_double
    {
        public double m_dValue;
    }
}
