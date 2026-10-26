using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HoF2VR
{
    /// Whole-world searches (FindObjectsOfType / Resources.FindObjectsOfTypeAll) cost milliseconds in the
    /// Dealer's room (docs/smoothness-2026-10-11.md fix #5): they are done once per scene load and the lists
    /// reused. Version changes whenever a scene is loaded or unloaded; users rescan when it changed (and drop
    /// destroyed entries themselves).
    internal static class SceneScan
    {
        public static int Version = 1;
        public static int Scans;    // diagnostics: whole-world searches done through here
        static bool s_hooked;

        public static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            SceneManager.sceneLoaded += (s, m) => Version++;
            SceneManager.sceneUnloaded += s => Version++;
        }

        static readonly List<Renderer> s_renderers = new List<Renderer>();
        static int s_rendererVersion = -1;

        /// Every active renderer of the loaded scenes as of the last scene change (objects created since then
        /// are missing; destroyed ones are null).
        public static List<Renderer> Renderers()
        {
            Hook();
            if (s_rendererVersion != Version)
            {
                s_rendererVersion = Version;
                s_renderers.Clear();
                s_renderers.AddRange(Object.FindObjectsOfType<Renderer>());
                Scans++;
            }
            return s_renderers;
        }

        /// Components of a type in the loaded scenes, inactive ones included (cached per scene change).
        public sealed class AllOf<T> where T : Component
        {
            readonly List<T> m_list = new List<T>();
            int m_version = -1;

            public List<T> Get()
            {
                Hook();
                if (m_version != Version)
                {
                    m_version = Version;
                    m_list.Clear();
                    foreach (T c in Resources.FindObjectsOfTypeAll<T>())
                        if (c != null && c.gameObject.scene.IsValid()) m_list.Add(c);
                    Scans++;
                }
                return m_list;
            }

            public void Invalidate() => m_version = -1;
        }
    }
}
