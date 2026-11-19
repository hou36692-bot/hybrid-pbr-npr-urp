using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Moves the Suntail Village pack from its own Shader Graph shaders onto URP Lit (Metallic/Roughness),
    /// which is the environment PBR baseline. Idempotent: only materials still on a Raygeas shader are touched,
    /// and the pack's original state stays recoverable from Git.
    ///
    /// What is carried over and what is not:
    /// Surface maps 1:1; Foliage keeps cutout and double-sided but loses wind, translucency and the
    /// two-tone gradient (approximated by a blended base color). Water becomes a transparent, smooth
    /// URP Lit surface: it sits at the edge of the scene and never in a showcase framing, so depth
    /// color, foam and refraction aren't worth a custom shader.
    /// </summary>
    public static class SuntailMaterialConverter
    {
        const string PackRoot = "Assets/Suntail Village";
        const string SurfaceShader = "Raygeas/Suntail Surface";
        const string FoliageShader = "Raygeas/Suntail Foliage";
        const string WaterShader = "Raygeas/Suntail Water";

        // Fraction of the second (tip) color mixed into a foliage material's flat base color.
        const float FoliageSecondColorWeight = 0.35f;

        [MenuItem("Tools/HybridRP/Convert Suntail Materials to URP Lit")]
        public static void ConvertFromMenu() => Debug.Log(Convert());

        /// <summary>Converts every remaining Surface/Foliage material and returns a report.</summary>
        public static string Convert()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                throw new InvalidOperationException("URP Lit shader not found.");

            var report = new StringBuilder();
            int surface = 0, foliage = 0, water = 0;
            var notes = new List<string>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { PackRoot }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null || material.shader == null)
                        continue;

                    switch (material.shader.name)
                    {
                        case SurfaceShader:
                            ConvertSurface(material, lit, notes);
                            surface++;
                            break;
                        case FoliageShader:
                            ConvertFoliage(material, lit);
                            foliage++;
                            break;
                        case WaterShader:
                            ConvertWater(material, lit);
                            water++;
                            break;
                        default:
                            continue;
                    }
                    EditorUtility.SetDirty(material);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.SaveAssets();

            report.AppendLine($"Converted {surface} Surface, {foliage} Foliage and {water} Water materials to URP Lit.");
            foreach (string note in notes)
                report.AppendLine("  note: " + note);
            return report.ToString();
        }

        static void ConvertSurface(Material m, Shader lit, List<string> notes)
        {
            // Read everything before the shader swap: properties that don't exist on URP Lit are dropped with it.
            Texture albedo = m.GetTexture("_Albedo");
            Vector2 albedoScale = m.GetTextureScale("_Albedo"), albedoOffset = m.GetTextureOffset("_Albedo");
            Color tint = m.GetColor("_Color");
            Texture normal = m.GetTexture("_Normal");
            float normalScale = m.GetFloat("_NormalScale");
            Texture metalSmooth = m.GetTexture("_MetallicSmoothness");
            float metallic = m.GetFloat("_Metallic");
            float smoothness = m.GetFloat("_SurfaceSmoothness");
            bool smoothnessFromAlbedo = m.GetFloat("_SmoothnessSource") > 0.5f;
            Texture emission = m.GetTexture("_Emission");
            Color emissionColor = m.GetColor("_EmissionColor");

            m.shader = lit;
            ResetToOpaque(m);

            m.SetTexture("_BaseMap", albedo);
            m.SetTextureScale("_BaseMap", albedoScale);
            m.SetTextureOffset("_BaseMap", albedoOffset);
            tint.a = 1f; // the pack stores 0 here; URP multiplies it into alpha
            m.SetColor("_BaseColor", tint);

            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", normalScale);
            }

            // Suntail: metallic = slider * mask.r, smoothness = (mask.a or albedo.a) * slider.
            // URP Lit: with a mask, metallic = mask.r (slider ignored) and smoothness = mask.a * slider.
            if (metalSmooth != null)
            {
                m.SetTexture("_MetallicGlossMap", metalSmooth);
                if (Mathf.Abs(metallic - 1f) > 0.01f)
                    notes.Add($"{m.name}: metallic slider {metallic:F2} is ignored when a mask is present (URP Lit uses the mask directly).");
            }
            else
            {
                m.SetFloat("_Metallic", metallic);
            }
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_SmoothnessTextureChannel", smoothnessFromAlbedo ? 1f : 0f);

            bool emissive = emissionColor.maxColorComponent > 0.001f;
            if (emissive)
            {
                m.SetColor("_EmissionColor", emissionColor);
                if (emission != null)
                    m.SetTexture("_EmissionMap", emission);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }

            ApplyKeywords(m);
        }

        static void ConvertFoliage(Material m, Shader lit)
        {
            Texture albedo = m.GetTexture("_Albedo");
            Vector2 albedoScale = m.GetTextureScale("_Albedo"), albedoOffset = m.GetTextureOffset("_Albedo");
            Color main = m.GetColor("_MainColor");
            Color second = m.GetColor("_SecondColor");
            bool hasSecond = m.GetFloat("_Color2Enable") > 0.5f;
            float smoothness = m.GetFloat("_Smoothness");
            float cutoff = m.GetFloat("_AlphaCutoff");

            m.shader = lit;
            ResetToOpaque(m);

            // The pack lerps a second color over the first (tips, or world height). Without that shader
            // the nearest flat equivalent is a blended base color.
            Color baseColor = hasSecond ? Color.Lerp(main, second, FoliageSecondColorWeight) : main;
            baseColor.a = 1f; // the pack stores 0 here; with alpha clip it would cut everything away

            m.SetTexture("_BaseMap", albedo);
            m.SetTextureScale("_BaseMap", albedoScale);
            m.SetTextureOffset("_BaseMap", albedoOffset);
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Cull", 0f);          // two-sided cards
            m.SetFloat("_AlphaClip", 1f);
            m.SetFloat("_Cutoff", cutoff);
            m.EnableKeyword("_ALPHATEST_ON");
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            ApplyKeywords(m, keepQueue: true);
        }

        static void ConvertWater(Material m, Shader lit)
        {
            Texture waves = m.GetTexture("_WavesNormal");
            Color shallow = m.GetColor("_Color1"), deep = m.GetColor("_Color2");
            float opacity = m.GetFloat("_Opacity");
            float smoothness = m.GetFloat("_Smoothness");
            float metallic = m.GetFloat("_Metallic");
            float normalIntensity = m.GetFloat("_WavesNormalIntensity");

            m.shader = lit;
            ResetToOpaque(m);

            Color color = Color.Lerp(shallow, deep, 0.5f);
            color.a = Mathf.Clamp01(opacity * 0.8f);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (waves != null)
            {
                m.SetTexture("_BumpMap", waves);
                m.SetFloat("_BumpScale", normalIntensity * 2f);
                m.SetTextureScale("_BumpMap", new Vector2(60f, 60f));
                m.SetTextureScale("_BaseMap", new Vector2(60f, 60f));
            }

            // Standard URP transparent setup: alpha blend, no depth write, shadow-less.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_ReceiveShadows", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            m.SetShaderPassEnabled("ShadowCaster", false);

            ApplyKeywords(m, keepQueue: true);
        }

        static void ResetToOpaque(Material m)
        {
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.SetFloat("_Cull", 2f);
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_SrcBlend", 1f);
            m.SetFloat("_DstBlend", 0f);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Opaque");
            m.renderQueue = -1;
        }

        // URP derives keywords (_NORMALMAP, _METALLICSPECGLOSSMAP, smoothness source, ...) from the
        // property values; its inspector does this through an internal helper, which we call directly.
        static MethodInfo s_updateMaterial;
        static object s_modifiedMaterial;

        static void ApplyKeywords(Material m, bool keepQueue = false)
        {
            if (s_updateMaterial == null)
            {
                var asm = typeof(UnityEditor.BaseShaderGUI).Assembly;
                var utils = asm.GetType("Unity.Rendering.Universal.ShaderUtils", true);
                var updateType = utils.GetNestedType("MaterialUpdateType", BindingFlags.NonPublic | BindingFlags.Public);
                s_modifiedMaterial = Enum.Parse(updateType, "ModifiedMaterial");
                s_updateMaterial = utils.GetMethod("UpdateMaterial",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(Material), updateType, typeof(UnityEngine.Object) }, null);
                if (s_updateMaterial == null)
                    throw new MissingMethodException("URP ShaderUtils.UpdateMaterial not found; the URP version changed.");
            }

            int queue = m.renderQueue;
            s_updateMaterial.Invoke(null, new[] { m, s_modifiedMaterial, null });
            if (keepQueue)
                m.renderQueue = queue;
        }
    }
}
