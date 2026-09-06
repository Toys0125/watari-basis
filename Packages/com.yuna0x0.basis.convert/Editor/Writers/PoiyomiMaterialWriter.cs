using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using yuna0x0.Basis.Convert.Pipeline;

namespace yuna0x0.Basis.Convert.Writers
{
    /// <summary>
    /// Swaps one Poiyomi material to an installed Poiyomi URP shader while preserving the
    /// material data Unity keeps serialized even when the source shader is missing or broken.
    /// </summary>
    public static class PoiyomiMaterialWriter
    {
        private static readonly string[][] TextureAliases =
        {
            new[] { "_MainTex", "_BaseMap" },
        };

        private static readonly string[][] ColorAliases =
        {
            new[] { "_Color", "_BaseColor" },
        };

        private static readonly string[][] FloatAliases =
        {
            new[] { "_Glossiness", "_Smoothness", "_GlossMapScale" },
            new[] { "_Parallax", "_HeightScale" },
        };

        private sealed class TextureValue
        {
            public Texture Texture;
            public Vector2 Scale = Vector2.one;
            public Vector2 Offset = Vector2.zero;
        }

        private sealed class Snapshot
        {
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly Dictionary<string, int> Ints = new Dictionary<string, int>();
            public readonly Dictionary<string, Vector4> Colors = new Dictionary<string, Vector4>();
            public readonly Dictionary<string, TextureValue> Textures =
                new Dictionary<string, TextureValue>();
            public readonly HashSet<string> Keywords = new HashSet<string>();
            public int RenderQueue;
            public bool EnableInstancing;
            public bool DoubleSidedGi;
            public MaterialGlobalIlluminationFlags GlobalIlluminationFlags;
        }

        public static bool Write(Material material, Shader targetShader, string undoName)
        {
            if (material == null || targetShader == null
                || !PoiyomiMaterialPlanner.IsPoiyomiUrpTarget(targetShader))
            {
                return false;
            }

            Snapshot snapshot = Capture(material);
            Undo.RecordObject(material, undoName);

            material.shader = targetShader;
            Restore(material, snapshot);
            EditorUtility.SetDirty(material);
            return true;
        }

        private static Snapshot Capture(Material material)
        {
            Snapshot snapshot = new Snapshot
            {
                RenderQueue = material.renderQueue,
                EnableInstancing = material.enableInstancing,
                DoubleSidedGi = material.doubleSidedGI,
                GlobalIlluminationFlags = material.globalIlluminationFlags,
            };

            SerializedObject serialized = new SerializedObject(material);
            try
            {
                ReadFloats(serialized, snapshot.Floats);
                ReadInts(serialized, snapshot.Ints);
                ReadColors(serialized, snapshot.Colors);
                ReadTextures(serialized, snapshot.Textures);
                ReadKeywords(serialized, snapshot.Keywords);
            }
            finally
            {
                serialized.Dispose();
            }

            return snapshot;
        }

        private static void Restore(Material material, Snapshot snapshot)
        {
            Shader shader = material.shader;

            foreach (KeyValuePair<string, float> property in snapshot.Floats)
            {
                if (material.HasProperty(property.Key))
                {
                    material.SetFloat(property.Key, property.Value);
                }
            }

            foreach (KeyValuePair<string, int> property in snapshot.Ints)
            {
                if (material.HasProperty(property.Key))
                {
                    material.SetInteger(property.Key, property.Value);
                }
            }

            foreach (KeyValuePair<string, Vector4> property in snapshot.Colors)
            {
                int index = shader.FindPropertyIndex(property.Key);
                if (index < 0)
                {
                    continue;
                }

                ShaderPropertyType type = shader.GetPropertyType(index);
                if (type == ShaderPropertyType.Color)
                {
                    material.SetColor(property.Key, property.Value);
                }
                else if (type == ShaderPropertyType.Vector)
                {
                    material.SetVector(property.Key, property.Value);
                }
            }

            foreach (KeyValuePair<string, TextureValue> property in snapshot.Textures)
            {
                if (!material.HasProperty(property.Key))
                {
                    continue;
                }

                material.SetTexture(property.Key, property.Value.Texture);
                material.SetTextureScale(property.Key, property.Value.Scale);
                material.SetTextureOffset(property.Key, property.Value.Offset);
            }

            RestoreTextureAliases(material, snapshot.Textures);
            RestoreColorAliases(material, snapshot.Colors);
            RestoreFloatAliases(material, snapshot.Floats);

            foreach (string keyword in snapshot.Keywords)
            {
                if (!string.IsNullOrEmpty(keyword))
                {
                    material.EnableKeyword(keyword);
                }
            }

            material.renderQueue = snapshot.RenderQueue;
            material.enableInstancing = snapshot.EnableInstancing;
            material.doubleSidedGI = snapshot.DoubleSidedGi;
            material.globalIlluminationFlags = snapshot.GlobalIlluminationFlags;
        }

        private static void RestoreTextureAliases(
            Material material, Dictionary<string, TextureValue> values)
        {
            foreach (string[] family in TextureAliases)
            {
                TextureValue value = First(values, family);
                if (value == null)
                {
                    continue;
                }

                foreach (string name in family)
                {
                    if (values.ContainsKey(name) || !material.HasProperty(name))
                    {
                        continue;
                    }

                    material.SetTexture(name, value.Texture);
                    material.SetTextureScale(name, value.Scale);
                    material.SetTextureOffset(name, value.Offset);
                }
            }
        }

        private static void RestoreColorAliases(
            Material material, Dictionary<string, Vector4> values)
        {
            foreach (string[] family in ColorAliases)
            {
                if (!TryFirst(values, family, out Vector4 value))
                {
                    continue;
                }

                foreach (string name in family)
                {
                    if (values.ContainsKey(name))
                    {
                        continue;
                    }

                    int index = material.shader.FindPropertyIndex(name);
                    if (index < 0)
                    {
                        continue;
                    }

                    ShaderPropertyType type = material.shader.GetPropertyType(index);
                    if (type == ShaderPropertyType.Color)
                    {
                        material.SetColor(name, value);
                    }
                    else if (type == ShaderPropertyType.Vector)
                    {
                        material.SetVector(name, value);
                    }
                }
            }
        }

        private static void RestoreFloatAliases(
            Material material, Dictionary<string, float> values)
        {
            foreach (string[] family in FloatAliases)
            {
                if (!TryFirst(values, family, out float value))
                {
                    continue;
                }

                foreach (string name in family)
                {
                    if (!values.ContainsKey(name) && material.HasProperty(name))
                    {
                        material.SetFloat(name, value);
                    }
                }
            }
        }

        private static TextureValue First(
            Dictionary<string, TextureValue> values, string[] names)
        {
            foreach (string name in names)
            {
                if (values.TryGetValue(name, out TextureValue value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool TryFirst<T>(Dictionary<string, T> values, string[] names, out T value)
        {
            foreach (string name in names)
            {
                if (values.TryGetValue(name, out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static void ReadFloats(
            SerializedObject serialized, Dictionary<string, float> into)
        {
            SerializedProperty array = serialized.FindProperty("m_SavedProperties.m_Floats");
            if (array == null || !array.isArray)
            {
                return;
            }

            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty entry = array.GetArrayElementAtIndex(i);
                string name = PoiyomiMaterialPlanner.PropertyName(entry);
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null)
                {
                    into[name] = value.floatValue;
                }
            }
        }

        private static void ReadInts(
            SerializedObject serialized, Dictionary<string, int> into)
        {
            SerializedProperty array = serialized.FindProperty("m_SavedProperties.m_Ints");
            if (array == null || !array.isArray)
            {
                return;
            }

            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty entry = array.GetArrayElementAtIndex(i);
                string name = PoiyomiMaterialPlanner.PropertyName(entry);
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null)
                {
                    into[name] = value.intValue;
                }
            }
        }

        private static void ReadColors(
            SerializedObject serialized, Dictionary<string, Vector4> into)
        {
            SerializedProperty array = serialized.FindProperty("m_SavedProperties.m_Colors");
            if (array == null || !array.isArray)
            {
                return;
            }

            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty entry = array.GetArrayElementAtIndex(i);
                string name = PoiyomiMaterialPlanner.PropertyName(entry);
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null)
                {
                    Color color = value.colorValue;
                    into[name] = new Vector4(color.r, color.g, color.b, color.a);
                }
            }
        }

        private static void ReadTextures(
            SerializedObject serialized, Dictionary<string, TextureValue> into)
        {
            SerializedProperty array = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
            if (array == null || !array.isArray)
            {
                return;
            }

            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty entry = array.GetArrayElementAtIndex(i);
                string name = PoiyomiMaterialPlanner.PropertyName(entry);
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (string.IsNullOrEmpty(name) || value == null)
                {
                    continue;
                }

                SerializedProperty texture = value.FindPropertyRelative("m_Texture");
                SerializedProperty scale = value.FindPropertyRelative("m_Scale");
                SerializedProperty offset = value.FindPropertyRelative("m_Offset");
                into[name] = new TextureValue
                {
                    Texture = texture?.objectReferenceValue as Texture,
                    Scale = scale != null ? scale.vector2Value : Vector2.one,
                    Offset = offset != null ? offset.vector2Value : Vector2.zero,
                };
            }
        }

        private static void ReadKeywords(SerializedObject serialized, HashSet<string> into)
        {
            SerializedProperty legacy = serialized.FindProperty("m_ShaderKeywords");
            if (legacy != null && legacy.propertyType == SerializedPropertyType.String
                && !string.IsNullOrEmpty(legacy.stringValue))
            {
                foreach (string keyword in legacy.stringValue.Split(' '))
                {
                    if (!string.IsNullOrEmpty(keyword))
                    {
                        into.Add(keyword);
                    }
                }
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
                        into.Add(entry.stringValue);
                    }
                }
            }
        }
    }
}
