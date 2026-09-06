using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using yuna0x0.Basis.Convert.Model;

namespace yuna0x0.Basis.Convert.Pipeline
{
    /// <summary>
    /// Finds Poiyomi materials used by the source prefabs and, when a Poiyomi URP shader is
    /// installed, chooses the base URP shader they can be migrated to. This stage only reads.
    /// </summary>
    public static class PoiyomiMaterialPlanner
    {
        public const string DiscordUrl = "https://discord.gg/poiyomi";

        private static readonly string[] SpecialtyShaderNames =
        {
            "grab pass", "grabpass", "outline", "fur", "cutout", "tessellated", "stencil", "overlay",
            "fake light", "transparent", "hidden/locked", "hidden/", "debug", "wireframe",
        };

        public static void Inspect(AvatarConversionPlan plan)
        {
            if (plan == null || plan.Sources.Count == 0)
            {
                return;
            }

            Dictionary<Material, PlannedPoiyomiMaterial> found =
                new Dictionary<Material, PlannedPoiyomiMaterial>();

            foreach (ConversionSource source in plan.Sources)
            {
                if (source?.Root == null)
                {
                    continue;
                }

                foreach (Renderer renderer in source.Root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!BelongsToSource(renderer, source.Root))
                    {
                        continue;
                    }

                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null || IsPoiyomiUrpMaterial(material)
                            || !IsPoiyomiMaterial(material))
                        {
                            continue;
                        }

                        if (found.TryGetValue(material, out PlannedPoiyomiMaterial existing))
                        {
                            if (existing != null && !existing.Sources.Contains(source))
                            {
                                existing.Sources.Add(source);
                            }
                            continue;
                        }

                        plan.PoiyomiMaterialsFound++;

                        if (!IsEditableProjectMaterial(material, out string reason))
                        {
                            plan.MaterialDiagnostics.Add(DiagnosticSeverity.Warning,
                                "material.poiyomiReadOnly",
                                $"'{material.name}' uses Poiyomi but cannot be changed: {reason}.");
                            found[material] = null;
                            continue;
                        }

                        PlannedPoiyomiMaterial planned = new PlannedPoiyomiMaterial
                        {
                            Material = material,
                            SourceShaderName = OriginalShaderName(material),
                        };
                        planned.Sources.Add(source);
                        found[material] = planned;
                        plan.PoiyomiMaterials.Add(planned);
                    }
                }
            }

            if (plan.PoiyomiMaterials.Count == 0)
            {
                return;
            }

            List<Shader> urpShaders = FindInstalledUrpShaders();
            int ready = 0;
            foreach (PlannedPoiyomiMaterial material in plan.PoiyomiMaterials)
            {
                material.TargetShader = SelectUrpReplacement(material.Material, urpShaders);
                if (material.TargetShader != null)
                {
                    ready++;
                }
            }

            int missing = plan.PoiyomiMaterials.Count - ready;
            if (missing > 0)
            {
                plan.MaterialDiagnostics.Add(DiagnosticSeverity.Warning,
                    "material.poiyomiUrpMissing",
                    $"{missing} Poiyomi material(s) need a compatible Poiyomi URP shader. "
                    + "Get the URP shader from the Poiyomi Discord, import it, then Rescan. "
                    + "Those materials will be left unchanged until it is present.");
            }

            if (ready == 0)
            {
                return;
            }

            plan.MaterialDiagnostics.Add(DiagnosticSeverity.Mapped,
                "material.poiyomiUrp",
                $"{ready} Poiyomi material(s) will be remapped to the installed Poiyomi URP "
                + "shader. Saved Poiyomi properties, textures, keywords and render queue are kept.");
        }

        /// <summary>
        /// A renderer inside a nested prefab belongs to that prefab's source, not to the parent
        /// prefab that happens to contain the instance.
        /// </summary>
        private static bool BelongsToSource(Renderer renderer, GameObject sourceRoot)
        {
            GameObject nested = PrefabUtility.GetNearestPrefabInstanceRoot(renderer.gameObject);
            return nested == null || nested == sourceRoot;
        }

        public static bool IsPoiyomiMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            Shader shader = material.shader;
            string shaderName = shader != null ? shader.name : string.Empty;
            string shaderPath = shader != null ? AssetDatabase.GetAssetPath(shader) : string.Empty;
            if (IsPoiyomiIdentity(shaderName, shaderPath))
            {
                return true;
            }

            SerializedObject serialized = new SerializedObject(material);
            try
            {
                foreach (string keyword in ReadKeywords(serialized))
                {
                    if (keyword.IndexOf("POIYOMI", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return SavedPropertyNameContains(serialized, "ThemeIndex");
            }
            finally
            {
                serialized.Dispose();
            }
        }

        public static bool IsPoiyomiIdentity(string shaderName, string assetPath)
        {
            return ContainsPoiyomi(shaderName) || ContainsPoiyomi(assetPath)
                || (!string.IsNullOrEmpty(shaderName)
                    && shaderName.IndexOf(".poyi", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrEmpty(assetPath)
                    && assetPath.IndexOf("_PoiyomiShaders", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsPoiyomiUrpShader(Shader shader)
        {
            if (shader == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(shader);
            return IsPoiyomiIdentity(shader.name, path) && IsUniversalPipelineShader(shader);
        }

        public static bool IsPoiyomiUrpTarget(Shader shader)
        {
            return IsPoiyomiUrpShader(shader)
                && !shader.name.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase)
                && !IsSpecialty(shader.name)
                && !ShaderUtil.ShaderHasError(shader);
        }

        private static bool IsPoiyomiUrpMaterial(Material material) =>
            material != null && IsPoiyomiUrpShader(material.shader);

        public static Shader SelectUrpReplacement(Material source, IReadOnlyList<Shader> candidates)
        {
            Shader best = null;
            int bestScore = int.MinValue;
            string sourceName = source?.shader != null ? source.shader.name.ToLowerInvariant() : string.Empty;

            if (candidates == null)
            {
                return null;
            }

            foreach (Shader candidate in candidates)
            {
                if (!IsPoiyomiUrpTarget(candidate))
                {
                    continue;
                }

                string name = candidate.name.ToLowerInvariant();
                int score = 0;
                if (name.Contains("pro")) score += 40;
                if (name.Contains("toon")) score += 30;
                if (name.Contains("urp")) score += 20;
                if (sourceName.Contains("pro") && name.Contains("pro")) score += 20;
                if (sourceName.Contains("toon") && name.Contains("toon")) score += 10;
                score += Math.Max(0, 100 - candidate.name.Length);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        public static List<Shader> FindInstalledUrpShaders()
        {
            List<Shader> result = new List<Shader>();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (IsPoiyomiUrpTarget(shader))
                {
                    result.Add(shader);
                }
            }

            return result;
        }

        private static bool IsUniversalPipelineShader(Shader shader)
        {
            bool sawTag = false;
            for (int i = 0; i < shader.subshaderCount; i++)
            {
                string pipeline = shader.FindSubshaderTagValue(
                    i, new ShaderTagId("RenderPipeline")).name;
                if (string.IsNullOrEmpty(pipeline))
                {
                    continue;
                }

                sawTag = true;
                if (pipeline == "UniversalPipeline" || pipeline == "UniversalRenderPipeline")
                {
                    return true;
                }
            }

            if (sawTag)
            {
                return false;
            }

            string name = shader.name.ToLowerInvariant();
            string path = AssetDatabase.GetAssetPath(shader).ToLowerInvariant();
            return name.Contains("urp") || name.Contains("universal render pipeline")
                || path.Contains("urp") || path.Contains("universal render pipeline");
        }

        private static bool IsSpecialty(string shaderName)
        {
            string name = shaderName.ToLowerInvariant();
            foreach (string pattern in SpecialtyShaderNames)
            {
                if (name.Contains(pattern))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsEditableProjectMaterial(Material material, out string reason)
        {
            reason = null;
            string path = AssetDatabase.GetAssetPath(material)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(path))
            {
                reason = "it is not a project asset";
                return false;
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                reason = path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                    ? "it belongs to a package"
                    : "it is outside Assets";
                return false;
            }

            if (!AssetDatabase.IsMainAsset(material)
                || !string.Equals(System.IO.Path.GetExtension(path), ".mat",
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "it is embedded in another imported asset; extract the material first";
                return false;
            }

            if ((material.hideFlags & HideFlags.NotEditable) != 0)
            {
                reason = "it is marked NotEditable";
                return false;
            }

            return true;
        }

        private static string OriginalShaderName(Material material)
        {
            if (material?.shader != null)
            {
                return material.shader.name;
            }
            return "(missing shader)";
        }

        private static bool ContainsPoiyomi(string value) =>
            !string.IsNullOrEmpty(value)
            && value.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0;

        private static List<string> ReadKeywords(SerializedObject serialized)
        {
            List<string> keywords = new List<string>();
            SerializedProperty legacy = serialized.FindProperty("m_ShaderKeywords");
            if (legacy != null && legacy.propertyType == SerializedPropertyType.String
                && !string.IsNullOrEmpty(legacy.stringValue))
            {
                keywords.AddRange(legacy.stringValue.Split(' '));
            }

            foreach (string field in new[] { "m_ValidKeywords", "m_InvalidKeywords" })
            {
                SerializedProperty array = serialized.FindProperty(field);
                if (array == null || !array.isArray)
                {
                    continue;
                }

                for (int i = 0; i < array.arraySize; i++)
                {
                    SerializedProperty entry = array.GetArrayElementAtIndex(i);
                    if (entry.propertyType == SerializedPropertyType.String
                        && !string.IsNullOrEmpty(entry.stringValue))
                    {
                        keywords.Add(entry.stringValue);
                    }
                }
            }

            return keywords;
        }

        private static bool SavedPropertyNameContains(
            SerializedObject serialized, string fragment)
        {
            foreach (string bucket in new[]
                     {
                         "m_SavedProperties.m_Floats",
                         "m_SavedProperties.m_Ints",
                         "m_SavedProperties.m_Colors",
                         "m_SavedProperties.m_TexEnvs",
                     })
            {
                SerializedProperty array = serialized.FindProperty(bucket);
                if (array == null || !array.isArray)
                {
                    continue;
                }

                for (int i = 0; i < array.arraySize; i++)
                {
                    string name = PropertyName(array.GetArrayElementAtIndex(i));
                    if (!string.IsNullOrEmpty(name)
                        && name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static string PropertyName(SerializedProperty entry)
        {
            SerializedProperty first = entry?.FindPropertyRelative("first");
            if (first == null)
            {
                return null;
            }

            if (first.propertyType == SerializedPropertyType.String)
            {
                return first.stringValue;
            }

            SerializedProperty name = first.FindPropertyRelative("name");
            return name != null && name.propertyType == SerializedPropertyType.String
                ? name.stringValue
                : null;
        }
    }
}
