#ifndef HYBRID_CHARACTER_LIGHTING_INCLUDED
#define HYBRID_CHARACTER_LIGHTING_INCLUDED

// Lighting evaluation shared by the Lit, Face, Hair and Eye forward passes: baked GI,
// the main light, the additional-light loop and the indirect (GI + reflection) term. The
// part shaders keep only what differs (SDF gate, hair band, lip highlight, eye response).

#include "HybridCharacterCommon.hlsl"

// Probe GI exactly as URP samples it for this variant. A macro, because the varyings
// carry staticLightmapUV or vertexSH/probeOcclusion depending on the variant and URP's
// SAMPLE_GI drops the arguments a variant does not use.
#if defined(LIGHTMAP_ON)
    #define HYBRID_SAMPLE_URP_GI(input, normalWS, viewDirWS, bakedGI, shadowMask) \
        bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, normalWS); \
        shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    // When the Stage 2 probe-GI override replaces the sample, URP's own per-pixel SampleAPV
    // would be thrown away: skip it so a fragment pays for one probe fetch, not two.
    #define HYBRID_SAMPLE_URP_GI(input, normalWS, viewDirWS, bakedGI, shadowMask) \
        if (HybridProbeOverrideActive()) \
        { \
            bakedGI = half3(0.0h, 0.0h, 0.0h); \
            shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h); \
        } \
        else \
            bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(input.positionWS), normalWS, \
                                viewDirWS, input.positionCS.xy, input.probeOcclusion, shadowMask);
#else
    #define HYBRID_SAMPLE_URP_GI(input, normalWS, viewDirWS, bakedGI, shadowMask) \
        bakedGI = SampleSH(normalWS);
#endif

Light HybridGetMainLight(float3 positionWS, half4 shadowMask)
{
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    Light mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS), positionWS, shadowMask);
#else
    Light mainLight = GetMainLight();
#endif
#ifdef _LIGHT_LAYERS
    if (!IsMatchingLightLayer(mainLight.layerMask, GetMeshRenderingLayer()))
        mainLight.color = 0;
#endif
    return mainLight;
}

// ---------------------------------------------------------------------------
// Self-shadow smoothing (portfolio pass)
// ---------------------------------------------------------------------------

// Main-light cascade shadow at an arbitrary world position. URP's MainLightRealtimeShadow takes a
// shadow coordinate built for the fragment, and with _MAIN_LIGHT_SHADOWS_SCREEN (our contact
// shadows) that coordinate is a screen UV, so it cannot be moved. The cascade map, matrices and
// split spheres stay bound in every variant, so this rebuilds the cascade lookup directly.
half HybridMainLightShadowAt(float3 positionWS)
{
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    #if defined(_MAIN_LIGHT_SHADOWS)
        half cascadeIndex = 0.0h;
    #else
        half cascadeIndex = ComputeCascadeIndex(positionWS);
    #endif
    float4 shadowCoord = float4(mul(_MainLightWorldToShadow[cascadeIndex], float4(positionWS, 1.0)).xyz, 0.0);
    half shadow = SampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare), shadowCoord,
                                  GetMainLightShadowSamplingData(), GetMainLightShadowParams(), false);
    return lerp(shadow, 1.0h, GetMainLightShadowFade(positionWS));
#else
    return 1.0h;
#endif
}

// Per-pixel CSM on an anime character paints strand-level hair shadow across the face and
// shoulders: dark speckled bands the ramp then hardens. Stylised characters (Endfield included)
// keep the large shadows (walls, roofs, the arm over the body) and drop the fine self-occlusion.
// Moving the lookup toward the light skips occluders within `offset` metres of the surface,
// which is exactly the strand/fold range. _HybridSelfShadowSmoothing = 0 is URP's own value.
half HybridSmoothedShadow(Light mainLight, float3 positionWS, half offset)
{
    // Uniform branch: scenes that leave the weight at 0 skip the second shadow-map lookup.
    if (_HybridSelfShadowSmoothing <= 0.0)
        return mainLight.shadowAttenuation;
    half smoothed = HybridMainLightShadowAt(positionWS + mainLight.direction * offset);
    return lerp(mainLight.shadowAttenuation, smoothed, saturate(_HybridSelfShadowSmoothing));
}

// Face and eyes take one shadow value for the whole head, sampled in front of the head toward
// the light: the face then enters shadow as a unit (under an eave) and the SDF alone draws its
// terminator, instead of the bangs' cast shadow striping the eyes.
half HybridHeadShadow(Light mainLight)
{
    if (_HybridSelfShadowSmoothing <= 0.0)
        return mainLight.shadowAttenuation;
    float3 headCentre = _HybridHeadPosition.xyz + _HybridHeadUp.xyz * 0.08;
    half head = HybridMainLightShadowAt(headCentre + mainLight.direction * 0.3);
    return lerp(mainLight.shadowAttenuation, head, saturate(_HybridSelfShadowSmoothing));
}

// ---------------------------------------------------------------------------
// Character/environment integration (Stage 2, S2-2)
// ---------------------------------------------------------------------------

// Normal of a vertical capsule around the character (centre and half-length published by
// HybridCharacterDriver). It is what the probe field "sees" at body scale: sides face the
// street, the head faces the sky, the hem faces the ground. axisPointWS is the point on the
// body axis at the fragment's own height, so a position lock keeps the vertical gradient.
float3 HybridCapsuleNormal(float3 positionWS, out float3 axisPointWS)
{
    float3 centre = _HybridCharacterCenter.xyz;
    float t = clamp(positionWS.y - centre.y, -_HybridCharacterCenter.w, _HybridCharacterCenter.w);
    axisPointWS = float3(centre.x, positionWS.y, centre.z);
    return SafeNormalize(positionWS - float3(centre.x, centre.y + t, centre.z));
}

// True when the override will replace URP's probe sample: APV variant, not lightmapped, no
// vertex/mixed SH evaluation (URP returns the vertex SH there), non-zero weights and a
// published capsule.
bool HybridProbeOverrideActive()
{
#if (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)) && !defined(LIGHTMAP_ON) \
    && !defined(EVALUATE_SH_VERTEX) && !defined(EVALUATE_SH_MIXED)
    return (_HybridGINormalFlatten + _HybridGIPositionLock) > 0.0 && _HybridCharacterCenter.w >= 0.0;
#else
    return false;
#endif
}

// Probe GI as the character reads it. URP evaluates APV per pixel at the fragment, with the
// mapped normal and a per-pixel position jitter. On a 1.9 m character in a 1 m probe grid that
// carries strand- and fold-level variation (normal maps), grain (the jitter) and a lateral leak
// where a wall probe is closer than the body; the ramp then quantises it into blotches. Here the
// position slides toward the body axis (height is kept, so the hem still sees the ground
// bounce) and the normal toward the capsule, which keeps the environment's colour and direction
// and drops the high frequencies. Both weights at 0 return URP's own value unchanged.
half3 HybridCharacterProbeGI(half3 urpGI, float3 positionWS, half3 normalWS, half3 viewDirWS)
{
#if (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)) && !defined(LIGHTMAP_ON)
    half flatten = saturate(_HybridGINormalFlatten);
    half positionLock = saturate(_HybridGIPositionLock);
    if (!HybridProbeOverrideActive())
        return urpGI;

    float3 axisPointWS;
    float3 capsuleNormal = HybridCapsuleNormal(positionWS, axisPointWS);
    float3 normal = SafeNormalize(lerp(float3(normalWS), capsuleNormal, flatten));
    float3 samplePositionWS = lerp(positionWS, axisPointWS, positionLock);

    float3 bakedGI;
    if (_EnableProbeVolumes)
    {
        APVSample apvSample = SampleAPV(GetAbsolutePositionWS(samplePositionWS), normal, GetMeshRenderingLayer(), viewDirWS);
        EvaluateAdaptiveProbeVolume(apvSample, normal, bakedGI);
    }
    else
    {
        bakedGI = EvaluateAmbientProbe(normal);
    }
    return half3(bakedGI);
#else
    return urpGI;
#endif
}

// Rims that tie the character to its surroundings, added on top of the material's own white
// Fresnel rim. Environment rim: the silhouette picks up the probe light around it, so the
// character's edge carries the colour of the place it stands in. Back rim: a main light behind
// the character (toward the camera) lights the edges facing it, the usual anime separation from
// a bright background. Both use the material's rim power and mask; Hybrid mode only.
half3 HybridIntegrationRim(half3 normalWS, half3 viewDirWS, Light mainLight, half3 probeGI, half power, half mask)
{
    if (_HybridShadingMode != 0 || mask <= 0.0h)
        return 0.0h;
    half fresnel = PositivePow(1.0h - saturate(dot(normalWS, viewDirWS)), max(0.5h, power));
    half3 environmentRim = probeGI * (fresnel * _HybridEnvRimWeight);

    half behind = saturate(dot(-viewDirWS, mainLight.direction));
    half facing = saturate(dot(normalWS, mainLight.direction) + 0.3h);
    half3 backRim = mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation *
                                       fresnel * behind * facing * _HybridBackRimWeight);
    return (environmentRim + backRim) * mask;
}

// Thin silhouette rim lit by the sun on the side facing it and by the environment elsewhere, so
// the character separates from a busy, similarly toned background (the courtyard's dark timber).
// The rim takes the surface colour (lerp toward white by 0.3): a white line on near-black hair
// and skin alike read as a halo pasted around the figure, not as light catching an edge.
half3 HybridScreenSpaceRim(half3 normalWS, float4 positionCS, float2 screenUV, Light mainLight, half3 probeGI,
                           half3 albedo, half mask)
{
    if (_HybridShadingMode != 0 || mask <= 0.0h)
        return 0.0h;
    half coverage = HybridScreenRim(normalWS, positionCS, screenUV);
    half towardLight = saturate(dot(normalWS, mainLight.direction) * 0.5h + 0.5h);
    half3 sun = mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation * towardLight * 0.5h);
    half3 tint = lerp(albedo, 1.0h, 0.3h);
    // Light-driven highlights: the environment's share shrinks so the rim sits where the sun is.
    half environment = lerp(0.6h, 0.2h, (half)saturate(_HybridHighlightLightDrive));
    return (sun + probeGI * environment) * tint * (coverage * _HybridScreenRimWeight * mask);
}

// The key light's colour, attenuation and shadow at unit luminance, for stylised highlights that
// should follow the light's hue, direction and shadow but not the scene's absolute sun intensity.
// Capped at 2 per channel: dividing a strongly saturated key (pure blue) by its luminance would
// otherwise boost that channel up to 14x.
half3 HybridKeyLightTint(Light light)
{
    half luminance = max(dot(light.color, half3(0.2126h, 0.7152h, 0.0722h)), 1e-3h);
    return min(light.color / luminance, 2.0h) * (light.distanceAttenuation * light.shadowAttenuation);
}

// Strand highlight (v4 pass). Kajiya-Kay on the head's meridian tangent (HybridEquirectTangent),
// with the tangent shifted along the normal per strand (Scheuermann's shift) so the lobe breaks
// along the strands instead of forming one ring. Returns sin(T,H)^exponent, with KK's directional
// attenuation so the lobe does not wrap to the far side.
half HybridStrandLobe(half3 tangentWS, half3 normalWS, half3 halfDir, half shift, half exponent)
{
    half3 t = SafeNormalize(tangentWS + normalWS * shift);
    half tDotH = dot(t, halfDir);
    half sinTH = sqrt(saturate(1.0h - tDotH * tDotH));
    half dirAtten = smoothstep(-1.0h, 0.0h, tDotH);
    return dirAtten * PositivePow(sinTH, max(1.0h, exponent));
}

// Endfield's hair reads near-black: the lit mass sits at roughly half its albedo and the highlights
// are a lighter shade of the hair (a soft grey-green band), never a white sheet. So both lobes take
// the hair's own hue: the primary "angel ring" is thin and masked by the authored strand map, the
// secondary is a broad sheen scaled by the albedo itself. albedoTint 0 = pure _HairHLColor.
half3 HybridHairStrandHighlight(half3 albedo, half3 bandNormalWS, half3 viewDirWS, Light mainLight,
                                half shiftNoise, half strandMask, half regionIntensity, half3 hlColor,
                                half hlIntensity, half hlExponent, half hlShift, half hlWidth, half albedoTint,
                                half sheenIntensity, half sheenExponent)
{
    half3 tangent = HybridEquirectTangent(bandNormalWS);
    half3 halfDir = SafeNormalize(mainLight.direction + viewDirWS);
    half shift = hlShift + (shiftNoise - 0.5h) * 0.6h;

    // Primary: thin lobe, edges tightened with smoothstep so it reads as a painted band.
    half primary = HybridStrandLobe(tangent, bandNormalWS, halfDir, shift, hlExponent);
    primary = smoothstep(1.0h - hlWidth, 1.0h, primary);
    // Secondary: Scheuermann's second lobe (Marschner's TRT), shifted apart, wider, hair-coloured.
    half secondary = HybridStrandLobe(tangent, bandNormalWS, halfDir, shift - 0.15h, sheenExponent);

    half3 hue = albedo / max(Max3(albedo.r, albedo.g, albedo.b), 0.02h);
    half3 primaryColor = hlColor * lerp(1.0h, hue, albedoTint);
    half facing = saturate(dot(bandNormalWS, mainLight.direction) * 0.5h + 0.5h);
    half3 radiance = mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation * facing);
    return radiance * (primaryColor * (primary * strandMask * regionIntensity * hlIntensity)
                       + albedo * (secondary * regionIntensity * sheenIntensity));
}

// The references show the hair's mass as a near-neutral dark grey-green while its accent layers, the
// teal underside and the red tips, stay clearly coloured. In the texture the accents are only about
// twice as chromatic as the mass, so the dark grade sank them into the black. Saturation is raised in
// proportion to the texel's own chroma (max-min over max, linear): the mass (~0.25) is untouched, the
// accents (0.55 and up) get the full gain. 1 = off.
half3 HybridHairAccentSaturation(half3 albedo, half gain)
{
    half peak = Max3(albedo.r, albedo.g, albedo.b);
    half chroma = (peak - Min3(albedo.r, albedo.g, albedo.b)) / max(peak, 1e-3h);
    half saturation = lerp(1.0h, gain, smoothstep(0.3h, 0.55h, chroma));
    half luma = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    return max(0.0h, lerp(luma, albedo, saturation));
}

// Additional lights (the realtime lanterns) with the same ramp/GGX response as the main
// light. The ramp uses pure half-Lambert: distance attenuation lives in the radiance,
// because inside the ramp coordinate it pushes distant lights into the black end.
// URP's Forward+ LIGHT_LOOP_BEGIN reads a local named inputData (position + screen UV).
void HybridAccumulateAdditionalLights(InputData inputData, half4 shadowMask, TEXTURE2D_PARAM(rampMap, rampSampler),
                                      half3 albedo, half3 diffuseAlbedo, half metallic,
                                      half3 specColor, half smoothness, half3 normalWS, half3 viewDirWS,
                                      half rampWeight, half specIntensity,
                                      inout half3 directDiffuse, inout half3 directSpecular)
{
#if (defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS)) && !defined(_ADDITIONAL_LIGHTS_VERTEX)
    half3 f0 = 0.04h * specColor + albedo * metallic;
    uint lightCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(lightCount)
        Light light = GetAdditionalLight(lightIndex, inputData.positionWS, shadowMask);
#ifdef _LIGHT_LAYERS
        if (!IsMatchingLightLayer(light.layerMask, GetMeshRenderingLayer()))
            continue;
#endif
        half NoL = dot(normalWS, light.direction);
        half shadow = light.shadowAttenuation;
        half3 radiance = light.color * light.distanceAttenuation;

        if (_HybridShadingMode == 1)
        {
            BRDFData brdf;
            half alpha = 1.0h;
            InitializeBRDFData(albedo, metallic, specColor, smoothness, alpha, brdf);
            directDiffuse += LightingPhysicallyBased(brdf, light, normalWS, viewDirWS);
        }
        else
        {
            // Explicit LOD: an implicit-gradient sample inside a varying-length loop is invalid.
            // diffuseAlbedo, not albedo: metal must answer the lanterns the way it answers the sun.
            half4 rampSample = SAMPLE_TEXTURE2D_LOD(rampMap, rampSampler, float2(saturate(NoL * 0.5h + 0.5h), 0.5h), 0);
            half3 rampDiffuse = diffuseAlbedo * rampSample.rgb * rampSample.a * shadow;
            half3 lambert = diffuseAlbedo * saturate(NoL) * shadow;
            directDiffuse += lerp(lambert, rampDiffuse, rampWeight) * radiance;
#if defined(_SPEC_ON)
            directSpecular += HybridGGXSpecular(f0, smoothness, normalWS, light.direction, viewDirWS,
                                                radiance, shadow, specIntensity);
#endif
        }
    LIGHT_LOOP_END
#endif
}

// Shadow lift: Endfield's character shadows stay readable and coloured in full daylight, because
// probe GI alone (sky + bounce, a fraction of the sun) leaves the shadow side several stops under
// the lit side. A share of the main light, tinted by the material's shadow colour, fills the
// shadowed area; it is the art-directed "shadow tone" of a cel ramp rather than physical light.
// shadowAmount is 1 in full shadow; 0 lift returns nothing.
half3 HybridShadowFill(half3 albedo, Light mainLight, half shadowAmount, half3 shadowColor)
{
    if (_HybridShadingMode != 0)
        return 0.0h;
    return albedo * shadowColor * mainLight.color *
           (mainLight.distanceAttenuation * saturate(shadowAmount) * saturate(_HybridShadowLift));
}

// Shadow colour: tint the shadowed indirect toward the material's shadow colour
// instead of multiplying it toward black. shadowAmount is 1 in full shadow. The environment's
// hue already reaches the shadow through the probe GI this multiplies, so the tint stays the
// material's art-directed colour.
half3 HybridShadowTint(half shadowAmount, half3 shadowColor, half strength)
{
    half3 tint = 1.0h;
#if defined(_SHADOWCOLOR_ON)
    if (_HybridShadingMode == 0)
        tint = lerp(1.0h, shadowColor, saturate(strength * _HybridShadowColorWeight * shadowAmount));
#endif
    return tint;
}

// Indirect diffuse (probe GI) plus glossy reflection probes through URP's EnvironmentBRDF,
// so the Hybrid and PBR paths share one indirect term.
half3 HybridIndirectLighting(half3 albedo, half metallic, half3 specColor, half smoothness, half3 normalWS,
                             half3 viewDirWS, float3 positionWS, float2 screenUV, half3 bakedGI, half ao,
                             half3 shadowTint, half reflectionScale)
{
    half3 indirectDiffuse = bakedGI * ao * shadowTint;
    half3 reflectVector = reflect(-viewDirWS, normalWS);
    half3 indirectSpecular = GlossyEnvironmentReflection(reflectVector, positionWS, 1.0h - smoothness, ao, screenUV)
                           * reflectionScale;
    half fresnelTerm = Pow4(1.0h - saturate(dot(normalWS, viewDirWS)));

    BRDFData brdfData;
    half alpha = 1.0h;
    InitializeBRDFData(albedo, metallic, specColor, smoothness, alpha, brdfData);
    return EnvironmentBRDF(brdfData, indirectDiffuse, indirectSpecular, fresnelTerm);
}

#endif // HYBRID_CHARACTER_LIGHTING_INCLUDED
