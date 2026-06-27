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
using EFT.AssetsManager;
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
        public int InstanceID;
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
        public List<Option<PatchedRenderer>> MountRenderers;
        public List<Option<PatchedRenderer>> ScopeRenderers;
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

    [BepInPlugin("7Bpencil.TransparentSights", "7Bpencil.TransparentSights", "0.0.1")]
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
            new Patch_AssetPoolObject_OnDestroy().Enable();
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

            return new()
            {
                { "616442e4faa1272e43152193", true  },
                { "6477772ea8a38bb2050ed4db", false },
                { "618a5d5852ecee1505530b2a", false },
                { "5b30b0dc5acfc400153b7124", false },
                { "5b3116595acfc40019476364", true  },
                { "58491f3324597764bc48fa02", false },
                { "59f9d81586f7744c7506ee62", false },
                { "58d399e486f77442e0016fe7", true  },
                { "6165ac8c290d254f5e6b2f6c", false },
                { "591c4efa86f7741030027726", false },
                { "58d268fc86f774111273f8c2", false },
                { "5d2da1e948f035477b1ce2ba", false },
                { "688b44cb28bf8d85cd0ff108", false },
                { "558022b54bdc2dac148b458d", false },
                { "570fd6c2d2720bc6458b457f", false },
                { "570fd721d2720bc5458b4596", false },
                { "570fd79bd2720bc7458b4583", false },
                { "57ae0171245977343c27bfcf", false },
                { "584924ec24597768f12ae244", false },
                { "584984812459776a704a82a6", false },
                { "5c0505e00db834001b735073", false },
                { "6113d6c3290d254f5e6b27db", false },
                { "6544d4187c5457729210d277", false },
                { "68a5ab09c44fa287ba0a97b5", false },
                { "655f13e0a246670fb0373245", false },
                { "64785e7c19d732620e045e15", false },
                { "60a23797a37c940de7062d02", false },
                { "609a63b6e2ff132951242d09", false },
                { "5c7d55de2e221644f31bff68", false },
                { "61659f79d92c473c770213ee", false },
                { "61657230d92c473c770213d7", true  },
                { "5a32aa8bc4a2826c6e06d737", false },
                { "68a5ac69b55a6b93c20a2bc7", false },
                { "688b4bd81cef2a61d0052738", false },
                { "5947db3f86f77447880cf76f", false },
                { "57486e672459770abd687134", false },
                { "577d141e24597739c5255e01", false },
                { "5c05295e0db834001a66acbb", false },
                { "5fb6564947ce63734e3fa1da", false },
                { "55d5f46a4bdc2d1b198b4567", false },
                { "5894a81786f77427140b8347", false },
                { "5ba26b17d4351e00367f9bdd", false },
                { "68a63d3c8e977b40b2032286", false },
                { "5fc0fa957283c4046c58147e", false },
                { "68caad70269e10396503ad00", false },
                { "5dfa3d7ac41b2312ea33362a", false },
                { "5bb20e49d4351e3bac1212de", false },
                { "5bc09a18d4351e003562b68e", false },
                { "5c1780312e221602b66cc189", false }
            };
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
            var instanceID = scopeTransform.gameObject.GetInstanceID();
            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                currentPatchedScope.InstanceID != instanceID)
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
                    MountRenderers = PatchScopeRenderers(mountTransform),
                    ScopeRenderers = PatchScopeRenderers(scopeTransform),
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
                InstanceID = instanceID,
                DOF = DOF,
                OriginalSettingsDOF = originalSettings
            });
            TweenScopeToAim(patchedScope, isMountTransparent, DOF);
        }

        public void OnAimingDisabled()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                PatchedScopes.TryGetValue(currentPatchedScope.InstanceID, out var patchedScope))
            {
                CurrentPatchedScope = default;
                TweenScopeFromAim(patchedScope, currentPatchedScope.DOF, currentPatchedScope.OriginalSettingsDOF);
            }
        }

        public List<Option<PatchedRenderer>> PatchScopeRenderers(Transform scopeTransform)
        {
            if (!scopeTransform)
            {
                return [];
            }
            if (scopeTransform.TryGetComponent<LODGroup>(out var scopeLodGroup))
            {
                return PatchScopeRenderers(scopeLodGroup);
            }

            var renderers = scopeTransform.GetComponentsInChildren<Renderer>();
            return PatchScopeRenderers(renderers);
        }

        public List<Option<PatchedRenderer>> PatchScopeRenderers(LODGroup lodGroup)
        {
            var result = new List<Option<PatchedRenderer>>();
            foreach (var lod in lodGroup.GetLODs())
            {
                foreach (var renderer in lod.renderers)
                {
					result.Add(PatchScopeRenderer(renderer));
                }
            }

            return result;
        }

        public List<Option<PatchedRenderer>> PatchScopeRenderers(Renderer[] renderers)
        {
            var result = new List<Option<PatchedRenderer>>(renderers.Length);
            foreach (var renderer in renderers)
            {
				result.Add(PatchScopeRenderer(renderer));
            }

            return result;
        }

		public Option<PatchedRenderer> PatchScopeRenderer(Renderer renderer)
		{
            if (!renderer)
            {
                return default;
            }
            var oldMaterials = renderer.materials;
            if (oldMaterials == null)
            {
                return default;
            }
            var newMaterials = new Material[oldMaterials.Length];
            for (var i = 0; i < oldMaterials.Length; i++)
            {
                var oldMaterial = oldMaterials[i];
                if (oldMaterial && IsOpaqueMaterial(oldMaterial))
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

            return new(new PatchedRenderer()
            {
                Renderer = renderer,
                Original = oldMaterials,
                Patched = newMaterials,
            });
		}

        public bool IsOpaqueMaterial(Material material)
        {
            var shaderName = material.shader.name;
            return
                shaderName == "p0/Reflective/Bumped Specular SMap" ||
                shaderName == "CW FX/BackLens";
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

        public void SetPatched(List<Option<PatchedRenderer>> patchedRenderers)
        {
            foreach (var patchedRendererOption in patchedRenderers)
            {
                if (patchedRendererOption.Some(out var patchedRenderer))
                {
                    patchedRenderer.Renderer.materials = patchedRenderer.Patched;
                }
            }
        }

        public void SetOriginal(List<Option<PatchedRenderer>> patchedRenderers)
        {
            foreach (var patchedRendererOption in patchedRenderers)
            {
                if (patchedRendererOption.Some(out var patchedRenderer))
                {
                    patchedRenderer.Renderer.materials = patchedRenderer.Original;
                }
            }
        }

        public void TweenScopeFromAim(PatchedScopeRenderers patchedScope, DepthOfField DOF, SettingsDOF originalSettingsDOF)
        {
            SetOriginal(patchedScope.ScopeRenderers);
            SetOriginal(patchedScope.MountRenderers);
            Set_DOF_parameters(DOF, originalSettingsDOF);
        }

        public void OnItemDestroy(AssetPoolObject assetPoolObject)
        {
            var instanceID = assetPoolObject.gameObject.GetInstanceID();

            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                currentPatchedScope.InstanceID == instanceID)
            {
                OnAimingDisabled();
            }

            if (PatchedScopes.Remove(instanceID, out var patchedScope))
            {
                CleanPatchedRenderers(patchedScope.MountRenderers);
                CleanPatchedRenderers(patchedScope.ScopeRenderers);
            }
        }

        public void CleanPatchedRenderers(List<Option<PatchedRenderer>> renderers)
        {
            foreach (var patchedRendererOption in renderers)
            {
                if (patchedRendererOption.Some(out var patchedRenderer))
                {
                    foreach (var patched in patchedRenderer.Patched)
                    {
                        Destroy(patched);
                    }
                }
            }

            renderers.Clear();
        }

    }
}
