using System.Text;
using UnityEngine;

namespace HoF2VR
{
    /// `terrains` diagnostic: terrains in the loaded scenes, their tree/detail settings and tree prototypes.
    internal class TerrainInfo : MonoBehaviour
    {
        void Awake()
        {
            Diag.Commands["terrains"] = a => Describe();
        }

        static string Describe()
        {
            var sb = new StringBuilder();
            foreach (Terrain t in Resources.FindObjectsOfTypeAll<Terrain>())
            {
                if (!t.gameObject.scene.IsValid()) continue;
                TerrainData d = t.terrainData;
                sb.Append("'").Append(t.name).Append("' scene=").Append(t.gameObject.scene.name)
                  .Append(" active=").Append(t.isActiveAndEnabled)
                  .Append(" trees=").Append(d != null ? d.treeInstanceCount : -1)
                  .Append(" drawTrees=").Append(t.drawTreesAndFoliage)
                  .Append(" treeDist=").Append(t.treeDistance).Append(" billboardDist=").Append(t.treeBillboardDistance)
                  .Append(" fullLOD=").Append(t.treeMaximumFullLODCount)
                  .Append(" detailDist=").Append(t.detailObjectDistance)
                  .Append(" size=").Append(d != null ? d.size.ToString() : "-").Append('\n');
                if (d == null) continue;
                foreach (TreePrototype p in d.treePrototypes)
                {
                    GameObject g = p.prefab;
                    sb.Append("    proto '").Append(g != null ? g.name : "null").Append("'");
                    if (g != null)
                    {
                        LODGroup lod = g.GetComponent<LODGroup>();
                        sb.Append(" lods=").Append(lod != null ? lod.lodCount : 0)
                          .Append(" renderers=").Append(g.GetComponentsInChildren<Renderer>(true).Length)
                          .Append(" colliders=").Append(g.GetComponentsInChildren<Collider>(true).Length);
                        Renderer r = g.GetComponentInChildren<Renderer>(true);
                        if (r != null && r.sharedMaterial != null) sb.Append(" shader=").Append(r.sharedMaterial.shader.name);
                    }
                    sb.Append('\n');
                }
            }
            Plugin.L.LogInfo("terrains:\n" + sb);
            return sb.Length == 0 ? "no terrains" : "see log";
        }
    }
}
