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
        public static readonly int _Cull = Shader.PropertyToID("_Cull");

        private const double SaveLagTime = 10;
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

		public ManualLogSource LoggerInstance;

        private string ConfigPath;
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
            DOF_maxBlurSize = Config.Bind<float>("Depth of Field", "Max Blur Size", 0.94f, new ConfigDescription("", new AcceptableValueRange<float>(0, 15)));

            DOF_enabled.SettingChanged += (_, _) => { Change_DOF(Set_DOF_enabled); };
            DOF_blurSampleCount.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_aperture.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_focalLength.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_focalSize.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_foregroundOverlap.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };
            DOF_maxBlurSize.SettingChanged += (_, _) => { Change_DOF(Set_DOF_parameters_config); };

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SightShader = Shader.Find("Transparent/DepthZwriteDithered");
            ConfigPath = Path.Combine(assemblyDir, "config.json");
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
            var oldMaterials = renderer.materials;
            var newMaterials = new Material[oldMaterials.Length];
            for (var i = 0; i < oldMaterials.Length; i++)
            {
                // TODO what if shader is different?
                var oldMaterial = oldMaterials[i];
				if (oldMaterial.shader.name == "p0/Reflective/Bumped Specular SMap")
                {
                    var newMaterial = new Material(SightShader);
                    newMaterial.CopyPropertiesFromMaterial(oldMaterial);
                    newMaterial.SetFloat(_Cull, 2); // set backface culling, because some scopes have front face culling for some reasons
                    newMaterials[i] = newMaterial;
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
            };
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

        public void TweenScopeFromAim(PatchedScopeRenderers patchedScope, DepthOfField DOF, SettingsDOF originalSettingsDOF)
        {
            SetOriginal(patchedScope.ScopeRenderers);
            SetOriginal(patchedScope.MountRenderers);
            Set_DOF_parameters(DOF, originalSettingsDOF);
        }

    }
}
