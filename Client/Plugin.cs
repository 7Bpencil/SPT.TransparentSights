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
using UnityEngine.Video;

namespace SevenBoldPencil.TransparentSights
{
    public struct PatchedScope
    {
        public string TemplateID;
        public int TransformInstanceID;
    }

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
        private const double SaveLagTime = 60;
		private static string[] ScopeTransparencyModeNames =
		[
			"TRANSP. OFF",
			"TRANSP. ON",
			"TRANSP. ON + MOUNT",
		];

        public static Plugin Instance;

        public static ConfigEntry<float> AimingScopeOpacity;

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

            AimingScopeOpacity = Config.Bind<float>("Main", "Aiming Scope Opacity", 0.5f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 1f)));
            AimingScopeOpacity.SettingChanged += (_, _) => { ChangeCurrentScopeAlpha(); };

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var bundlePath = Path.Combine(assemblyDir, "bundles", "transparent-sights");
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            SightShader = bundle.LoadAsset<Shader>("Assets/TransparentSights/Shaders/Bumped Specular SMap.shader");
            ConfigPath = Path.Combine(assemblyDir, "transparent-sights-config.json");
            TransparentScopes = LoadTransparentScopes(ConfigPath);
            ScopesItemPanels = new();
            PatchedScopes = new();

            new Patch_PWA_method_23().Enable();
            new Patch_ItemSpecificationPanel_Show().Enable();
            new Patch_ItemSpecificationPanel_Close().Enable();
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
				var mountTransform = scopeTransform.parent.parent;
                if (scopeTransform && scopeTransform.TryGetComponent<LODGroup>(out var scopeLodGroup) &&
                    mountTransform && mountTransform.TryGetComponent<LODGroup>(out var mountLodGroup))
                {
                    patchedScope = new PatchedScopeRenderers()
                    {
                        MountRenderers = PatchScopeRendererLODs(mountLodGroup),
                        ScopeRenderers = PatchScopeRendererLODs(scopeLodGroup),
                    };
                    PatchedScopes.Add(instanceID, patchedScope);
                }
                else
                {
                    Logger.LogWarning($"Failed to get LODGroup for scope and its mount: {scopeTemplateId}, {scopeTransform.gameObject.name}");
                    return;
                }
            }

            // TODO handle case when player quickly switches ads
            // TODO get tween time from ergo/weight etc

            CurrentPatchedScope = new(new()
            {
                TemplateID = scopeTemplateId,
                TransformInstanceID = instanceID
            });
            StartCoroutine(TweenScopeToAim(patchedScope, isMountTransparent));
        }

        public void OnAimingDisabled()
        {
            if (CurrentPatchedScope.Some(out var currentPatchedScope) &&
                PatchedScopes.TryGetValue(currentPatchedScope.TransformInstanceID, out var patchedScope))
            {
                CurrentPatchedScope = default;
                StartCoroutine(TweenScopeFromAim(patchedScope));
            }
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

        // TODO tweening alpha looks weird when lighting on scope dont 100% match
        // TODO make it look closer to original
        public IEnumerator TweenScopeToAim(PatchedScopeRenderers patchedScope, bool isMountTransparent)
        {
            var fromAlpha = 1f;
            var toAlpha = AimingScopeOpacity.Value;
            var tweenTime = 0.25;
            var startTime = Time.realtimeSinceStartupAsDouble;

            SetPatched(patchedScope.ScopeRenderers);
            if (isMountTransparent)
            {
                SetPatched(patchedScope.MountRenderers);
            }

            // yield return null;

            // var t = InverseLerp(startTime, startTime + tweenTime, Time.realtimeSinceStartupAsDouble);
            // while (t < 1f)
            // {
            //     var alpha = (float)Lerp(fromAlpha, toAlpha, t);
            //     SetAlpha(patchedScope, alpha);
            //     yield return null;
            //     t = InverseLerp(startTime, startTime + tweenTime, Time.realtimeSinceStartupAsDouble);
            // }

            SetAlpha(patchedScope.ScopeRenderers, toAlpha);
            if (isMountTransparent)
            {
                SetAlpha(patchedScope.MountRenderers, toAlpha);
            }

            yield break;
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

        public void SetAlpha(List<PatchedRenderer> patchedRenderers, float alpha)
        {
            foreach (var patchedRenderer in patchedRenderers)
            {
                foreach (var patchedMaterial in patchedRenderer.Patched)
                {
    				if (patchedMaterial.shader.name == SightShader.name)
                    {
                        patchedMaterial.color = patchedMaterial.color.WithAlpha(alpha);
                    }
                }
            }
        }

        public IEnumerator TweenScopeFromAim(PatchedScopeRenderers patchedScope)
        {
            var fromAlpha = AimingScopeOpacity.Value;
            var toAlpha = 1f;
            var tweenTime = 0.25;
            var startTime = Time.realtimeSinceStartupAsDouble;

            // yield return null;

            // var t = InverseLerp(startTime, startTime + tweenTime, Time.realtimeSinceStartupAsDouble);
            // while (t < 1f)
            // {
            //     var alpha = (float)Lerp(fromAlpha, toAlpha, t);
            //     SetAlpha(patchedScope, alpha);
            //     yield return null;
            //     t = InverseLerp(startTime, startTime + tweenTime, Time.realtimeSinceStartupAsDouble);
            // }

            // SetAlpha(patchedScope, toAlpha);

            SetOriginal(patchedScope.ScopeRenderers);
            SetOriginal(patchedScope.MountRenderers);

            yield break;
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

            // TODO what if tweening?
            var toAlpha = AimingScopeOpacity.Value;

            SetAlpha(patchedScope.ScopeRenderers, toAlpha);
            if (isMountTransparent)
            {
                SetAlpha(patchedScope.MountRenderers, toAlpha);
            }
        }
    }
}
