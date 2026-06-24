//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.WeaponModding;
using Newtonsoft.Json;
using SevenBoldPencil.Common;
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityStandardAssets.ImageEffects;
using UnityEngine.Video;
using BlurSampleCount = UnityStandardAssets.ImageEffects.DepthOfField.BlurSampleCount;

namespace SevenBoldPencil.TransparentSights
{
    public struct PatchedScope
    {
        public string TemplateID;
        public int TransformInstanceID;
        public DepthOfField DOF;
        public SettingsDOF OriginalSettingsDOF;
    }

    public readonly record struct SettingsDOF
    (
        bool enabled,
        BlurSampleCount blurSampleCount,
        float aperture,
        float focalLength,
        float focalSize,
        float foregroundOverlap,
        float maxBlurSize
    );

    public struct PatchedScopeRenderers
    {
        public List<PatchedRenderer> MountRenderers;
        public List<PatchedRenderer> ScopeRenderers;
    }

    public struct PatchedRenderer
    {
        public Renderer Renderer;
        public Material[] Original;
        public Material[] Patched;
        public Texture[] OriginalTextures;
        public RenderTexture[] PatchedTextures;
        // TODO destroy created materials and textures
    }

	public enum ScopeTransparencyMode
	{
		Disabled,
		Enabled,
		EnabledWithMount,
		MODES_COUNT,
	}

    [BepInPlugin("7Bpencil.TransparentSights", "7Bpencil.TransparentSights", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static readonly int _MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int _ColorMultiplier = Shader.PropertyToID("_ColorMultiplier");
        public static readonly int _Alpha = Shader.PropertyToID("_Alpha");
        public static readonly int _OpacityScale = Shader.PropertyToID("_OpacityScale");

        private const double SaveLagTime = 60;
		private static string[] ScopeTransparencyModeNames =
		[
			"TRANSP. OFF",
			"TRANSP. ON",
			"TRANSP. ON + MOUNT",
		];

        public static Plugin Instance;

        public static ConfigEntry<bool> DOF_enabled;
        public static ConfigEntry<BlurSampleCount> DOF_blurSampleCount;
        public static ConfigEntry<float> DOF_aperture;
        public static ConfigEntry<float> DOF_focalLength;
        public static ConfigEntry<float> DOF_focalSize;
        public static ConfigEntry<float> DOF_foregroundOverlap;
        public static ConfigEntry<float> DOF_maxBlurSize;
        public static ConfigEntry<float> Scope_colorMultiplier;
        public static ConfigEntry<float> Scope_alpha;
        public static ConfigEntry<float> Scope_opacityScale;

		public ManualLogSource LoggerInstance;

        private string ConfigPath;
        private Material ChangeColorMaterial;
        private Shader SightShader;
        private Dictionary<string, bool> TransparentScopes;
        private Dictionary<string, Dictionary<ItemSpecificationPanel, ContextMenuButton>> ScopesItemPanels;
        private Dictionary<int, PatchedScopeRenderers> PatchedScopes;
        private Option<PatchedScope> CurrentPatchedScope;
        private Option<double> LastSaveTime;

        private void Awake()
        {
            Instance = this;
			LoggerInstance = Logger;

            DOF_enabled = Config.Bind<bool>("Depth of Field", "Enabled", true);
            DOF_blurSampleCount = Config.Bind<BlurSampleCount>("Depth of Field", "Blur Sample Count", BlurSampleCount.High);
            DOF_aperture = Config.Bind<float>("Depth of Field", "Aperture", 4, new ConfigDescription("", new AcceptableValueRange<float>(0, 50)));
            DOF_focalLength = Config.Bind<float>("Depth of Field", "Focal Length", 1.53f, new ConfigDescription("", new AcceptableValueRange<float>(0, 100)));
            DOF_focalSize = Config.Bind<float>("Depth of Field", "Focal Size", 0.61f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));
            DOF_foregroundOverlap = Config.Bind<float>("Depth of Field", "Foreground Overlap", 2.63f, new ConfigDescription("", new AcceptableValueRange<float>(0, 10)));
            DOF_maxBlurSize = Config.Bind<float>("Depth of Field", "Max Blur Size", 3.75f, new ConfigDescription("", new AcceptableValueRange<float>(0, 100)));
            Scope_colorMultiplier = Config.Bind<float>("Scope", "Color Multiplier", 0.25f, new ConfigDescription("", new AcceptableValueRange<float>(0, 1)));
            Scope_alpha = Config.Bind<float>("Scope", "Alpha", 0.25f, new ConfigDescription("", new AcceptableValueRange<float>(0, 1)));
            Scope_opacityScale = Config.Bind<float>("Scope", "Opacity Scale", 1f, new ConfigDescription("", new AcceptableValueRange<float>(0, 4)));

            DOF_enabled.SettingChanged += (_, _) => { Change_DOF(Set_DOF_enabled); };
            DOF_blurSampleCount.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_aperture.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_focalLength.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_focalSize.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_foregroundOverlap.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_maxBlurSize.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            Scope_colorMultiplier.SettingChanged += (_, _) => { ChangeCurrentScopeAlpha(); };
            Scope_alpha.SettingChanged += (_, _) => { ChangeCurrentScopeAlpha(); };
            Scope_opacityScale.SettingChanged += (_, _) => { ChangeCurrentScopeAlpha(); };

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var bundlePath = Path.Combine(assemblyDir, "bundles", "transparent-sights");
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            var changeColorShader = bundle.LoadAsset<Shader>("Assets/TransparentSights/Shaders/ChangeColor.shader");
            ChangeColorMaterial = new Material(changeColorShader);
            // SightShader = Shader.Find("Transparent/DepthZwriteDithered");
            SightShader = Shader.Find("Assets/TransparentSights/Custom Shaders/Tarkov Custom/Metallic/Lit/Transparent/Epic Transparent No AClip ZWrite_Icon.shader");
            ConfigPath = Path.Combine(assemblyDir, "transparent-sights-config.json");
            TransparentScopes = LoadTransparentScopes(ConfigPath);
            ScopesItemPanels = new();
            PatchedScopes = new();

            new Patch_PWA_method_23().Enable();
            new Patch_ItemSpecificationPanel_Show().Enable();
            new Patch_ItemSpecificationPanel_Close().Enable();
        }

        public void Change_DOF(Action<DepthOfField> change)
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                change(currentPatchedScope.DOF);
            }
        }

        public void Change_DOF(Action<DepthOfField, SettingsDOF> change)
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                change(currentPatchedScope.DOF, currentPatchedScope.OriginalSettingsDOF);
            }
        }

        public void Set_DOF_enabled(DepthOfField DOF, SettingsDOF originalSettingsDOF)
        {
            if (DOF_enabled.Value)
            {
                Set_DOF_parameters_config(DOF);
            }
            else
            {
                Set_DOF_parameters(DOF, originalSettingsDOF);
            }
        }

        public void Set_DOF_parameters_config(DepthOfField DOF)
        {
            DOF.enabled = DOF_enabled.Value;
            DOF.blurSampleCount = DOF_blurSampleCount.Value;
            DOF.aperture = DOF_aperture.Value;
            DOF.focalLength = DOF_focalLength.Value;
            DOF.focalSize = DOF_focalSize.Value;
            DOF.foregroundOverlap = DOF_foregroundOverlap.Value;
            DOF.maxBlurSize = DOF_maxBlurSize.Value;
        }

        public void Set_DOF_parameters(DepthOfField DOF, SettingsDOF settings)
        {
            DOF.enabled = settings.enabled;
            DOF.blurSampleCount = settings.blurSampleCount;
            DOF.aperture = settings.aperture;
            DOF.focalLength = settings.focalLength;
            DOF.focalSize = settings.focalSize;
            DOF.foregroundOverlap = settings.foregroundOverlap;
            DOF.maxBlurSize = settings.maxBlurSize;
        }

        public Dictionary<string, bool> LoadTransparentScopes(string filePath)
        {
            if (SafeIO.ReadAllText(filePath).Ok(out var json, out var e))
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json);
                return result;
            }
            else
            {
                Logger.LogError($"Failed to load transparent scopes, rolling back to default config: {e}");
            }

            return new();
        }

        public void SaveTransparentScopesToFile(string filePath, Dictionary<string, bool> transparentScopes)
        {
            var json = JsonConvert.SerializeObject(transparentScopes, Formatting.Indented);
            SafeIO.WriteAllTextAsync(filePath, json);
        }

        public Material FaceShieldGlassMaterial;
        public void Update()
        {
            // SaveLagTime and LastSaveTime are needed to not write to file
            // every time user changes scope transparency mode

            if (LastSaveTime.Some(out var lastSaveTime))
            {
                if (Time.realtimeSinceStartupAsDouble - lastSaveTime >= SaveLagTime)
                {
                    SaveTransparentScopesToFile(ConfigPath, TransparentScopes);
                    LastSaveTime = default;
                }
            }

            // TODO nocheckin
            if (Input.GetKeyDown(KeyCode.F13))
            {
                var scene = SceneManager.GetSceneByName("CommonUIScene");
                if (scene.IsValid())
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var target = GetItem(root.transform);
                        if (target)
                        {
                            foreach (var material in target.GetComponent<MeshRenderer>().materials)
                            {
                                if (material.shader.name == SightShader.name)
                                {
                                    FaceShieldGlassMaterial = material;
                                    Logger.LogWarning("FOUND MATERIAL");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        public Transform GetItem(Transform root)
        {
            var target = root.Find("Rotator/Positioned Object/Preview Pivot/item_equipment_helmet_opsCore_handgun_face_shield/axis/helmet_ops_core_handgun_face_shield_LOD0");
            if (target)
            {
                return target;
            }

            return root.Find("Rotator/Positioned Object/Preview Pivot/item_equipment_helmet_opsCore_handgun_face_shield(Clone)/axis/helmet_ops_core_handgun_face_shield_LOD0");
        }

        public static ScopeTransparencyMode GetNextMode(ScopeTransparencyMode value)
        {
            return (ScopeTransparencyMode)(((int)value + 1) % (int)ScopeTransparencyMode.MODES_COUNT);
        }

		private string GetName(ScopeTransparencyMode value)
		{
			return ScopeTransparencyModeNames[(int)value];
		}

        public ScopeTransparencyMode GetScopeTransparencyMode(string scopeTemplateId)
        {
            if (TransparentScopes.TryGetValue(scopeTemplateId, out var isMountTransparent))
            {
                if (isMountTransparent)
                {
                    return ScopeTransparencyMode.EnabledWithMount;
                }

                return ScopeTransparencyMode.Enabled;
            }

            return ScopeTransparencyMode.Disabled;
        }

        public string GetScopeTransparencyModeName(string scopeTemplateId)
        {
            var mode = GetScopeTransparencyMode(scopeTemplateId);
            var modeName = GetName(mode);
            return modeName;
        }

        public void SwitchScopeTransparencyMode(string scopeTemplateId)
        {
            if (TransparentScopes.TryGetValue(scopeTemplateId, out var isMountTransparent))
            {
                if (isMountTransparent)
                {
                    TransparentScopes.Remove(scopeTemplateId);
                }
                else
                {
                    TransparentScopes[scopeTemplateId] = true;
                }
            }
            else
            {
                TransparentScopes.Add(scopeTemplateId, false);
            }

            LastSaveTime = new(Time.realtimeSinceStartupAsDouble);
        }

        public void AddPanel(string scopeTemplateId, ItemSpecificationPanel panel, ContextMenuButton toggleButton)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Add(panel, toggleButton);
            }
            else
            {
                ScopesItemPanels.Add(scopeTemplateId, new(){{panel, toggleButton}});
            }
        }

        public void RemovePanel(string scopeTemplateId, ItemSpecificationPanel panel)
        {
            if (ScopesItemPanels.TryGetValue(scopeTemplateId, out var panels))
            {
                panels.Remove(panel);
                if (panels.Count == 0)
                {
                    ScopesItemPanels.Remove(scopeTemplateId);
                }
            }
        }

        public void UpdateAllPanels(string scopeTemplateId)
        {
            var modeName = GetScopeTransparencyModeName(scopeTemplateId);
            var panels = ScopesItemPanels[scopeTemplateId];
            foreach (var (panel, toggleButton) in panels)
            {
                new ContextMenuButton_Proxy(toggleButton)._text.text = modeName;
                panel.method_5();
            }
        }

        public void OnAimingEnabled(string scopeTemplateId, Transform scopeTransform)
        {
            var instanceID = scopeTransform.GetInstanceID();
            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                currentPatchedScope.TransformInstanceID != instanceID)
            {
                OnAimingDisabled();
            }

            if (!TransparentScopes.TryGetValue(scopeTemplateId, out var isMountTransparent))
            {
                return;
            }

            if (!PatchedScopes.TryGetValue(instanceID, out var patchedScope))
            {
                // TODO do we need to patch mount renderers if setting is off?
				var mountTransform = scopeTransform.parent.parent;
                patchedScope = new PatchedScopeRenderers()
                {
                    MountRenderers = PatchScopeRendererLODs(mountTransform),
                    ScopeRenderers = PatchScopeRendererLODs(scopeTransform),
                };
                PatchedScopes.Add(instanceID, patchedScope);
                Logger.LogWarning($"Patched scope: {scopeTemplateId}, {scopeTransform.gameObject.name}");
            }

            var DOF = CameraClass.Instance.DepthOfField_0;
            var originalSettings = new SettingsDOF
            (
                enabled: DOF.enabled,
                blurSampleCount: DOF.blurSampleCount,
                aperture: DOF.aperture,
                focalLength: DOF.focalLength,
                focalSize: DOF.focalSize,
                foregroundOverlap: DOF.foregroundOverlap,
                maxBlurSize: DOF.maxBlurSize
            );
            CurrentPatchedScope = new(new()
            {
                TemplateID = scopeTemplateId,
                TransformInstanceID = instanceID,
                DOF = DOF,
                OriginalSettingsDOF = originalSettings
            });
            TweenScopeToAim(patchedScope, isMountTransparent, DOF);
        }

        public void OnAimingDisabled()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                PatchedScopes.TryGetValue(currentPatchedScope.TransformInstanceID, out var patchedScope))
            {
                CurrentPatchedScope = default;
                TweenScopeFromAim(patchedScope, currentPatchedScope.DOF, currentPatchedScope.OriginalSettingsDOF);
            }
        }

        public List<PatchedRenderer> PatchScopeRendererLODs(Transform scopeTransform)
        {
            if (scopeTransform && scopeTransform.TryGetComponent<LODGroup>(out var scopeLodGroup))
            {
                return PatchScopeRendererLODs(scopeLodGroup);
            }

            return [];
        }

        public List<PatchedRenderer> PatchScopeRendererLODs(LODGroup lodGroup)
        {
            var result = new List<PatchedRenderer>();
            foreach (var lod in lodGroup.GetLODs())
            {
                foreach (var renderer in lod.renderers)
                {
					result.Add(PatchScopeRenderer(renderer));
                }
            }

            return result;
        }

		public PatchedRenderer PatchScopeRenderer(Renderer renderer)
		{
            var colorMultiplier = Scope_colorMultiplier.Value;
            var alpha = Scope_alpha.Value;
            var opacityScale = Scope_opacityScale.Value;

            var oldMaterials = renderer.materials;
            var newMaterials = new Material[oldMaterials.Length];
            var oldTextures = new Texture[oldMaterials.Length];
            var newTextures = new RenderTexture[oldMaterials.Length];
            for (var i = 0; i < oldMaterials.Length; i++)
            {
                // TODO what if shader is different?
                var oldMaterial = oldMaterials[i];
				if (oldMaterial.shader.name == "p0/Reflective/Bumped Specular SMap")
                {
                    var oldTexture = oldMaterial.GetTexture(_MainTex);
                    var newTexture = CreateRenderTexture(oldTexture);
                    PatchTexture(newTexture, oldTexture, colorMultiplier, alpha);
                    oldTextures[i] = oldTexture;
                    newTextures[i] = newTexture;

                    var newMaterial = new Material(SightShader);
                    newMaterial.CopyPropertiesFromMaterial(oldMaterial);
                    newMaterial.SetTexture(_MainTex, newTexture);
                    newMaterial.SetFloat(_OpacityScale, opacityScale);
                    newMaterials[i] = newMaterial;
                    // newMaterials[i] = FaceShieldGlassMaterial;
                }
                else
                {
                    newMaterials[i] = oldMaterial;
                }
            }

            return new PatchedRenderer()
            {
                Renderer = renderer,
                Original = oldMaterials,
                Patched = newMaterials,
                OriginalTextures = oldTextures,
                PatchedTextures = newTextures,
            };
		}

        public static RenderTexture CreateRenderTexture(Texture texture)
        {
            // use parameters of original texture
            var renderTexture = new RenderTexture(texture.width, texture.height, 0);
            renderTexture.anisoLevel = texture.anisoLevel;
            renderTexture.filterMode = texture.filterMode;
            renderTexture.wrapMode = texture.wrapMode;
            return renderTexture;
        }

        public void PatchTexture(RenderTexture renderTexture, Texture source, float colorMultiplier, float alpha)
        {
            ChangeColorMaterial.SetFloat(_ColorMultiplier, colorMultiplier);
            ChangeColorMaterial.SetFloat(_Alpha, alpha);
            ChangeColorMaterial.SetTexture(_MainTex, source);
            Graphics.Blit(null, renderTexture, ChangeColorMaterial);
            ChangeColorMaterial.SetTexture(_MainTex, null);
        }

        public void TweenScopeToAim(PatchedScopeRenderers patchedScope, bool isMountTransparent, DepthOfField DOF)
        {
            SetPatched(patchedScope.ScopeRenderers);
            if (isMountTransparent)
            {
                SetPatched(patchedScope.MountRenderers);
            }

            if (DOF_enabled.Value)
            {
                Set_DOF_parameters_config(DOF);
            }
        }

        public void SetPatched(List<PatchedRenderer> patchedRenderers)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                patchedRenderer.Renderer.materials = patchedRenderer.Patched;
            }
        }

        public void SetOriginal(List<PatchedRenderer> patchedRenderers)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                patchedRenderer.Renderer.materials = patchedRenderer.Original;
            }
        }

        public void PatchTextures(List<PatchedRenderer> patchedRenderers, float colorMultiplier, float alpha, float opacityScale)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                var patchedMaterials = patchedRenderer.Patched;
                var originalTextures = patchedRenderer.OriginalTextures;
                var patchedTextures = patchedRenderer.PatchedTextures;
                for (var i = 0; i < patchedMaterials.Length; i++)
                {
                    var patchedMaterial = patchedMaterials[i];
    				if (patchedMaterial.shader.name == SightShader.name)
                    {
                        var originalTexture = originalTextures[i];
                        var patchedTexture = patchedTextures[i];
                        patchedMaterial.SetFloat(_OpacityScale, opacityScale);
                        PatchTexture(patchedTexture, originalTexture, colorMultiplier, alpha);
                    }
                }
            }
        }

        public void TweenScopeFromAim(PatchedScopeRenderers patchedScope, DepthOfField DOF, SettingsDOF originalSettingsDOF)
        {
            SetOriginal(patchedScope.ScopeRenderers);
            SetOriginal(patchedScope.MountRenderers);
            Set_DOF_parameters(DOF, originalSettingsDOF);
        }

        public static double InverseLerp(double a, double b, double value)
        {
            return (value - a) / (b - a);
        }

        public static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        public void ChangeCurrentScopeAlpha()
        {
            if (!CurrentPatchedScope.Some(out var currentPatchedScope))
            {
                return;
            }
            if (!TransparentScopes.TryGetValue(currentPatchedScope.TemplateID, out var isMountTransparent))
            {
                return;
            }
            if (!PatchedScopes.TryGetValue(currentPatchedScope.TransformInstanceID, out var patchedScope))
            {
                return;
            }

            var colorMultiplier = Scope_colorMultiplier.Value;
            var alpha = Scope_alpha.Value;
            var opacityScale = Scope_opacityScale.Value;

            PatchTextures(patchedScope.ScopeRenderers, colorMultiplier, alpha, opacityScale);
            if (isMountTransparent)
            {
                PatchTextures(patchedScope.MountRenderers, colorMultiplier, alpha, opacityScale);
            }
        }
    }
}
