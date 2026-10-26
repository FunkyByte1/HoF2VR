using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// `comp <Type> [field=value ...]` - dumps the simple fields of every live component of a game
    /// type (private ones too) to the log, and optionally sets bool/int/float/enum fields on all of them.
    /// For runtime experiments only.
    internal static class Inspect
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Register()
        {
            Diag.Commands["comp"] = Comp;
            Diag.Commands["sget"] = SGet;
            // `sset <Type> <member> <value>` - set a static property/field (bool/int/float/enum)
            Diag.Commands["sset"] = a =>
            {
                if (a.Length < 4) return "usage: sset <Type> <member> <value>";
                Type t = Find(a[1]);
                if (t == null) return "no such type " + a[1];
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo p = t.GetProperty(a[2], S);
                if (p != null && p.CanWrite) { p.SetValue(null, Parse(p.PropertyType, a[3]), null); return a[1] + "." + a[2] + " = " + p.GetValue(null, null); }
                FieldInfo f = t.GetField(a[2], S);
                if (f != null) { f.SetValue(null, Parse(f.FieldType, a[3])); return a[1] + "." + a[2] + " = " + f.GetValue(null); }
                return "no writable static " + a[2];
            };
            // `camfx <Type> 0|1` - switch one image effect on the primary VR camera only
            Diag.Commands["camfx"] = a =>
            {
                Camera cam = VRRig.I != null ? VRRig.I.PrimaryCamera : null;
                if (cam == null || a.Length < 3) return "usage: camfx <Type> 0|1 (needs a primary camera)";
                Type t = Find(a[1]);
                Behaviour b = t != null ? cam.GetComponent(t) as Behaviour : null;
                if (b == null) return "no " + a[1] + " on " + cam.name;
                b.enabled = a[2] == "1";
                return a[1] + " on " + cam.name + " enabled=" + b.enabled;
            };
        }

        /// `sget <Type> <member>` - reads a static field or property, e.g. `sget QualitySettings activeColorSpace`.
        static string SGet(string[] a)
        {
            if (a.Length < 3) return "usage: sget <Type> <member>";
            Type t = Find(a[1]);
            if (t == null) return "no such type " + a[1];
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                PropertyInfo p = t.GetProperty(a[2], S);
                if (p != null) return a[1] + "." + a[2] + " = " + Convert.ToString(p.GetValue(null, null), Inv);
                FieldInfo f = t.GetField(a[2], S);
                if (f != null) return a[1] + "." + a[2] + " = " + Convert.ToString(f.GetValue(null), Inv);
            }
            catch (Exception e) { return "error: " + e.GetBaseException().Message; }
            return "no static member " + a[2];
        }

        static Type Find(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetTypes().FirstOrDefault(x => x.Name == name || x.FullName == name); }
                catch (ReflectionTypeLoadException) { continue; }
                if (t != null) return t;
            }
            return null;
        }

        static string Comp(string[] a)
        {
            if (a.Length < 2) return "usage: comp <Type> [field=value ...]";
            Type t = Find(a[1]);
            if (t == null || !typeof(UnityEngine.Object).IsAssignableFrom(t)) return "no such object type " + a[1];
            UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(t)
                .Where(o => !(o is Component c) || c.gameObject.scene.IsValid()).ToArray();
            var sb = new StringBuilder();
            foreach (UnityEngine.Object o in objs)
            {
                string where = o is Component c ? VRUI.Path(c.transform) + (c is Behaviour b ? (b.isActiveAndEnabled ? " (on)" : " (off)") : "") : o.name;
                sb.Append(t.Name).Append(" @ ").Append(where).Append('\n');
                for (int i = 2; i < a.Length; i++)
                {
                    string[] kv = a[i].Split('=');
                    if (kv.Length == 2) sb.Append("  set ").Append(kv[0]).Append(": ").Append(Set(o, kv[0], kv[1])).Append('\n');
                }
                for (Type ty = t; ty != null && ty != typeof(MonoBehaviour) && ty != typeof(Behaviour) && ty != typeof(Component); ty = ty.BaseType)
                {
                    foreach (FieldInfo f in ty.GetFields(All | BindingFlags.DeclaredOnly))
                    {
                        Type ft = f.FieldType;
                        if (!(ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(Color) || ft == typeof(Vector2) || ft == typeof(Vector3)
                              || typeof(UnityEngine.Object).IsAssignableFrom(ft))) continue;
                        object v;
                        try { v = f.GetValue(o); } catch { continue; }
                        string s = v is UnityEngine.Object uo ? (uo != null ? uo.name : "null") : v is float fl ? fl.ToString("0.###", Inv) : Convert.ToString(v, Inv);
                        sb.Append("  ").Append(f.Name).Append(" = ").Append(s).Append('\n');
                    }
                }
            }
            Plugin.L.LogInfo("comp " + a[1] + ":\n" + sb);
            return objs.Length + " object(s) (see log)";
        }

        static string Set(object o, string field, string value)
        {
            FieldInfo f = null;
            for (Type ty = o.GetType(); ty != null && f == null; ty = ty.BaseType) f = ty.GetField(field, All | BindingFlags.DeclaredOnly);
            if (f != null)
            {
                object v = Parse(f.FieldType, value);
                if (v == null) return "unsupported type " + f.FieldType.Name;
                f.SetValue(o, v);
                return "ok";
            }
            PropertyInfo p = o.GetType().GetProperty(field, All);
            if (p != null && p.CanWrite)
            {
                object v = Parse(p.PropertyType, value);
                if (v == null) return "unsupported type " + p.PropertyType.Name;
                p.SetValue(o, v, null);
                return "ok (property)";
            }
            return "no such field";
        }

        static object Parse(Type t, string s)
        {
            if (t == typeof(bool)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "on";
            if (t == typeof(int)) return int.Parse(s, Inv);
            if (t == typeof(float)) return float.Parse(s, Inv);
            if (t.IsEnum) return Enum.Parse(t, s, true);
            return null;
        }
    }
}
