using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using yuna0x0.Basis.Convert.Model;
using yuna0x0.Basis.Convert.Pipeline;
using yuna0x0.Basis.Convert.Writers;

namespace yuna0x0.Basis.Convert.Tests
{
    public class PoiyomiMaterialTests
    {
        private const string Root = "Assets/WatariPoiyomiTests";
        private const string SourcePath = Root + "/PoiyomiSource.shader";
        private const string TargetPath = Root + "/PoiyomiUrp.shader";
        private const string OutlinePath = Root + "/PoiyomiUrpOutline.shader";
        private const string LockedPath = Root + "/PoiyomiUrpLocked.shader";
        private const string GenericPath = Root + "/Generic.shader";

        private Shader _source;
        private Shader _target;
        private Shader _outline;
        private Shader _locked;
        private Shader _generic;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.CreateFolder("Assets", "WatariPoiyomiTests");
            }

            File.WriteAllText(SourcePath, ShaderText("Poiyomi/Test Toon", false, false));
            File.WriteAllText(TargetPath, ShaderText("Poiyomi Pro URP/Test Toon", true, true));
            File.WriteAllText(OutlinePath, ShaderText("Poiyomi Pro URP/Test Outline", true, true));
            File.WriteAllText(LockedPath, ShaderText("Hidden/Poiyomi Locked URP/Test", true, true));
            File.WriteAllText(GenericPath, ShaderText("Watari/Test Generic", true, false));
            AssetDatabase.Refresh();

            _source = AssetDatabase.LoadAssetAtPath<Shader>(SourcePath);
            _target = AssetDatabase.LoadAssetAtPath<Shader>(TargetPath);
            _outline = AssetDatabase.LoadAssetAtPath<Shader>(OutlinePath);
            _locked = AssetDatabase.LoadAssetAtPath<Shader>(LockedPath);
            _generic = AssetDatabase.LoadAssetAtPath<Shader>(GenericPath);

            Assert.That(_source, Is.Not.Null);
            Assert.That(_target, Is.Not.Null);
            Assert.That(_outline, Is.Not.Null);
            Assert.That(_locked, Is.Not.Null);
            Assert.That(_generic, Is.Not.Null);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Root);
        }

        [Test]
        public void PoiyomiIdentityRecognisesNamesAndInstallPaths()
        {
            Assert.That(PoiyomiMaterialPlanner.IsPoiyomiIdentity("Poiyomi Toon", string.Empty),
                Is.True);
            Assert.That(PoiyomiMaterialPlanner.IsPoiyomiIdentity(
                "Hidden/Locked/Avatar", "Assets/_PoiyomiShaders/Shaders/locked.shader"), Is.True);
            Assert.That(PoiyomiMaterialPlanner.IsPoiyomiIdentity(
                "Universal Render Pipeline/Lit", "Packages/com.unity.render-pipelines.universal/Lit.shader"),
                Is.False);
        }

        [Test]
        public void SerializedPoiyomiDataSurvivesAResetShaderAndIsDetected()
        {
            Material material = new Material(_source);
            try
            {
                material.SetFloat("_ThemeIndex", 7f);
                material.shader = _generic;

                Assert.That(material.shader, Is.EqualTo(_generic));
                Assert.That(PoiyomiMaterialPlanner.IsPoiyomiMaterial(material), Is.True,
                    "Poiyomi properties survive a missing/reset shader and still identify the material.");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SerializedPoiyomiKeywordAlsoDetectsAResetMaterial()
        {
            Material material = new Material(_generic);
            SerializedObject serialized = new SerializedObject(material);
            try
            {
                SerializedProperty invalid = serialized.FindProperty("m_InvalidKeywords");
                Assert.That(invalid, Is.Not.Null);
                invalid.arraySize++;
                invalid.GetArrayElementAtIndex(invalid.arraySize - 1).stringValue =
                    "POIYOMI_TEST_ON";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(PoiyomiMaterialPlanner.IsPoiyomiMaterial(material), Is.True);
            }
            finally
            {
                serialized.Dispose();
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ReplacementRequiresPoiyomiUrpAndRejectsSpecialtyVariants()
        {
            Material material = new Material(_source);
            try
            {
                Assert.That(PoiyomiMaterialPlanner.SelectUrpReplacement(
                    material, new List<Shader> { _source, _generic }), Is.Null,
                    "BIRP Poiyomi and unrelated URP shaders are not fallbacks.");

                Assert.That(PoiyomiMaterialPlanner.IsPoiyomiUrpShader(_locked), Is.True,
                    "A locked Poiyomi URP shader is already compatible.");
                Assert.That(PoiyomiMaterialPlanner.IsPoiyomiUrpTarget(_locked), Is.False,
                    "A locked shader must not become another material's conversion target.");
                Assert.That(PoiyomiMaterialPlanner.SelectUrpReplacement(
                    material, new List<Shader> { _locked, _outline, _target }), Is.EqualTo(_target),
                    "The base Poiyomi URP toon shader wins over locked and specialty shaders.");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void PlannerFindsPoiyomiAndInstalledUrpTarget()
        {
            string materialPath = Root + "/Avatar.mat";
            string prefabPath = Root + "/Avatar.prefab";
            Material material = new Material(_source);
            GameObject avatar = new GameObject("Avatar");
            try
            {
                AssetDatabase.CreateAsset(material, materialPath);
                avatar.AddComponent<MeshRenderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(avatar, prefabPath);

                AvatarConversionPlan plan = AvatarConversionPlanner.Plan(prefabPath);

                Assert.That(plan.PoiyomiMaterialsFound, Is.EqualTo(1));
                Assert.That(plan.PoiyomiMaterials.Count, Is.EqualTo(1));
                Assert.That(plan.PoiyomiMaterials[0].Material, Is.EqualTo(material));
                Assert.That(plan.PoiyomiMaterials[0].TargetShader, Is.Not.Null);
                Assert.That(PoiyomiMaterialPlanner.IsPoiyomiUrpShader(
                    plan.PoiyomiMaterials[0].TargetShader), Is.True);
                Assert.That(plan.MaterialDiagnostics.HasCode("material.poiyomiUrp"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void MaterialTargetAndDiagnosticsFollowTheMaterialsOption()
        {
            Material material = new Material(_source);
            try
            {
                AvatarConversionPlan plan = new AvatarConversionPlan();
                plan.PoiyomiMaterials.Add(new PlannedPoiyomiMaterial
                {
                    Material = material,
                    TargetShader = _target,
                });
                plan.MaterialDiagnostics.Add(DiagnosticSeverity.Mapped,
                    "material.test", "Material diagnostic.");

                Assert.That(plan.TotalPlanned, Is.EqualTo(1));
                Assert.That(plan.SelectedPoiyomiMaterialCount, Is.EqualTo(1));
                Assert.That(plan.PoiyomiUrpReadyCount, Is.EqualTo(1));
                Assert.That(plan.PoiyomiUrpMissingCount, Is.Zero);
                Assert.That(plan.TotalSelected, Is.EqualTo(1));
                Assert.That(plan.SelectedDiagnostics().HasCode("material.test"), Is.True);

                plan.Options.Materials = false;

                Assert.That(plan.SelectedPoiyomiMaterialCount, Is.Zero);
                Assert.That(plan.PoiyomiUrpReadyCount, Is.EqualTo(1),
                    "Installation state is independent of whether material conversion is selected.");
                Assert.That(plan.PoiyomiUrpMissingCount, Is.Zero);
                Assert.That(plan.TotalSelected, Is.Zero);
                Assert.That(plan.SelectedDiagnostics().HasCode("material.test"), Is.False);
                Assert.That(plan.AllDiagnostics().HasCode("material.test"), Is.True);

                plan.PoiyomiMaterials[0].TargetShader = null;
                Assert.That(plan.PoiyomiUrpReadyCount, Is.Zero);
                Assert.That(plan.PoiyomiUrpMissingCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SharedMaterialIsNotSelectedWhenAnyUsingSourceIsExcluded()
        {
            Material material = new Material(_source);
            try
            {
                ConversionSource avatar = new ConversionSource { Include = true };
                ConversionSource clothing = new ConversionSource { Include = false };
                PlannedPoiyomiMaterial shared = new PlannedPoiyomiMaterial
                {
                    Material = material,
                    TargetShader = _target,
                };
                shared.Sources.Add(avatar);
                shared.Sources.Add(clothing);

                AvatarConversionPlan plan = new AvatarConversionPlan();
                plan.PoiyomiMaterials.Add(shared);

                Assert.That(plan.SelectedPoiyomiMaterialCount, Is.Zero,
                    "Changing a shared material asset would also change an excluded prefab.");

                clothing.Include = true;
                Assert.That(plan.SelectedPoiyomiMaterialCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void WriterKeepsPoiyomiPropertiesTexturesKeywordsAndQueue()
        {
            Material material = new Material(_source);
            Texture2D texture = new Texture2D(2, 2);
            try
            {
                Color color = new Color(0.2f, 0.4f, 0.6f, 0.8f);
                material.SetFloat("_ThemeIndex", 5f);
                material.SetFloat("_Value", 0.37f);
                material.SetFloat("_Glossiness", 0.61f);
                material.SetInteger("_ModeInt", 3);
                material.SetColor("_Color", color);
                material.SetTexture("_MainTex", texture);
                material.SetTextureScale("_MainTex", new Vector2(2.5f, 3.5f));
                material.SetTextureOffset("_MainTex", new Vector2(0.25f, 0.5f));
                material.EnableKeyword("POIYOMI_TEST_ON");
                material.renderQueue = 2477;
                material.enableInstancing = true;
                material.doubleSidedGI = true;

                Assert.That(PoiyomiMaterialWriter.Write(material, _target, "Test Poiyomi remap"),
                    Is.True);
                Assert.That(material.shader, Is.EqualTo(_target));
                Assert.That(material.GetFloat("_ThemeIndex"), Is.EqualTo(5f));
                Assert.That(material.GetFloat("_Value"), Is.EqualTo(0.37f).Within(0.0001f));
                Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(0.61f).Within(0.0001f),
                    "Common BIRP/URP aliases are migrated like the reference converter.");
                Assert.That(material.GetInteger("_ModeInt"), Is.EqualTo(3));
                Assert.That(material.GetColor("_Color"), Is.EqualTo(color));
                Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(color));
                Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(texture));
                Assert.That(material.GetTexture("_BaseMap"), Is.EqualTo(texture));
                Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2.5f, 3.5f)));
                Assert.That(material.GetTextureScale("_BaseMap"), Is.EqualTo(new Vector2(2.5f, 3.5f)));
                Assert.That(material.GetTextureOffset("_MainTex"), Is.EqualTo(new Vector2(0.25f, 0.5f)));
                Assert.That(material.GetTextureOffset("_BaseMap"), Is.EqualTo(new Vector2(0.25f, 0.5f)));
                Assert.That(material.IsKeywordEnabled("POIYOMI_TEST_ON"), Is.True);
                Assert.That(material.renderQueue, Is.EqualTo(2477));
                Assert.That(material.enableInstancing, Is.True);
                Assert.That(material.doubleSidedGI, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
            }
        }

        private static string ShaderText(string name, bool urp, bool urpAliases)
        {
            string pipeline = urp ? @"""RenderPipeline""=""UniversalPipeline"" " : string.Empty;
            string aliases = urpAliases
                ? @"        _Smoothness (""Smoothness"", Float) = 0
        _BaseColor (""Base Color"", Color) = (1,1,1,1)
        _BaseMap (""Base Map"", 2D) = ""white"" {}
"
                : string.Empty;
            return $@"Shader ""{name}""
{{
    Properties
    {{
        _ThemeIndex (""Theme"", Float) = 0
        _Value (""Value"", Float) = 0
        _Glossiness (""Glossiness"", Float) = 0
        _ModeInt (""Mode Int"", Integer) = 0
        _Color (""Color"", Color) = (1,1,1,1)
        _MainTex (""Texture"", 2D) = ""white"" {{}}
{aliases}    }}
    SubShader
    {{
        Tags {{ {pipeline}""RenderType""=""Opaque"" }}
        Pass
        {{
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local POIYOMI_TEST_ON
            struct Attributes {{ float4 positionOS : POSITION; }};
            struct Varyings {{ float4 positionCS : SV_POSITION; }};
            Varyings vert(Attributes input) {{ Varyings output; output.positionCS = input.positionOS; return output; }}
            half4 frag(Varyings input) : SV_Target {{ return half4(1,1,1,1); }}
            ENDHLSL
        }}
    }}
}}";
        }
    }
}
