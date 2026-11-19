using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HybridRP.EditorTools
{
    /// <summary>
    /// Import settings for the character's game-style textures, chosen by file-name suffix.
    /// Data maps must stay linear and must not go through Unity's normal-map compression:
    /// the _N/_HN maps store XY only (B = 0), which the NormalMap type would corrupt.
    /// Suffixes: _D/_E colour (sRGB); _N/_HN/_P/_ST/_M data (linear); _RD/_RS/_lut_ ramps (clamped,
    /// no mips, uncompressed); _SDF the face threshold map (linear, uncompressed).
    /// </summary>
    public class TextureImportRules : AssetPostprocessor
    {
        const string CharacterRoot = "Assets/Characters/";
        const string TextureFolder = "/Textures/";

        enum Kind { Color, Data, Ramp, Sdf, Unmanaged }

        // Bump when the rules change: Unity then reimports textures that were imported under the old rules.
        public override uint GetVersion() => 2;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(CharacterRoot, StringComparison.Ordinal) ||
                !assetPath.Contains(TextureFolder))
                return;

            var kind = Classify(Path.GetFileNameWithoutExtension(assetPath));
            if (kind == Kind.Unmanaged)
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = kind == Kind.Color;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.maxTextureSize = 4096;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = kind == Kind.Color || kind == Kind.Data ? 4 : 1;

            switch (kind)
            {
                case Kind.Ramp:
                    // 1D lookups: mips or wrapping would bleed the ends of the ramp together.
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    break;
                case Kind.Sdf:
                    // Thresholded per pixel, so block compression shows up as stepped shadow edges.
                    importer.mipmapEnabled = true;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    break;
                default:
                    importer.mipmapEnabled = true;
                    // Matcaps and atlases are sampled at their borders; Repeat would bleed the opposite edge in.
                    importer.wrapMode = UsesClampedUv(Path.GetFileNameWithoutExtension(assetPath))
                        ? TextureWrapMode.Clamp
                        : TextureWrapMode.Repeat;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;
            }
        }

        static bool UsesClampedUv(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("_matcap_") || n.Contains("_atlas_");
        }

        static Kind Classify(string name)
        {
            string n = name.ToLowerInvariant();

            if (n.EndsWith("_sdf")) return Kind.Sdf;
            // _lut_ strips are 1D/2D lookups, so they get ramp handling (clamp, no mips, uncompressed).
            // They are imported linear like the other ramps; if a LUT turns out to be authored as sRGB
            // tint colors once a shader reads it, split it into its own kind.
            if (n.EndsWith("_rd") || n.EndsWith("_rs") || n.Contains("_lut_")) return Kind.Ramp;
            if (n.Contains("_matcap_")) return Kind.Color;
            if (n.EndsWith("_n") || n.EndsWith("_hn") || n.EndsWith("_p") || n.EndsWith("_st") ||
                n.EndsWith("_m") || n.EndsWith("_mro"))
                return Kind.Data;
            if (n.EndsWith("_d") || n.EndsWith("_e") || n.StartsWith("t_zfy_"))
                return Kind.Color;
            return Kind.Unmanaged;
        }
    }
}
