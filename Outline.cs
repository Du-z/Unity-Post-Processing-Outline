using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

[Serializable]
[PostProcess(typeof(OutlineRenderer), PostProcessEvent.AfterStack, "Custom/Outline", true)]
public sealed class Outline : PostProcessEffectSettings
{
    /// <summary>
    /// the color of the outline
    /// alpha = fill alpha; does not effect outline alpha;
    /// </summary>
    [ColorUsage(true), Tooltip("The custom color of the outline. The alpha is used for the for fill, not the outline.")]
    public ColorParameter OutlineColor = new ColorParameter { value = new Color(1,0,0,.05f) };

    [Header("Blur Settings")]
    [Range(0.0f, 1.0f), Tooltip("Renders the outline at half resolution")]
    public BoolParameter Downsample = new BoolParameter { value = true};
    [Range(0.0f, 3.0f), Tooltip("The size of the outline")]
    public FloatParameter BlurSize = new FloatParameter { value = 1.0f};

    // The objects to add outlines too
    [NonSerialized]
    public readonly ParameterOverride<Dictionary<GameObject, List<Renderer>>> ObjectRenderers = new ParameterOverride<Dictionary<GameObject, List<Renderer>>>
    {
        overrideState = true,
        value = new Dictionary<GameObject, List<Renderer>>()
    };

    public void ReplaceRenderers(IEnumerable<GameObject> gos)
    {
        ClearRenderers();
        AddRenderers(gos);
    }

    public void AddRenderers(IEnumerable<GameObject> gos)
    {
        foreach (var go in gos)
        {
            AddRenderers(go);
        }
    }

    public void AddRenderers(GameObject go)
    {
        var list = new List<Renderer>();
        go.GetComponentsInChildren(true, list);
        ObjectRenderers.value[go] = list;
    }

    public void RemoveRenderers(GameObject go)
    {
        ObjectRenderers.value.Remove(go);
    }

    public void ClearRenderers()
    {
        ObjectRenderers.value.Clear();
    }

    public override bool IsEnabledAndSupported(PostProcessRenderContext context)
    {
        return ObjectRenderers.value.Any() && base.IsEnabledAndSupported(context);
    }
}

#if UNITY_2017_1_OR_NEWER
[UnityEngine.Scripting.Preserve]
#endif
public sealed class OutlineRenderer : PostProcessEffectRenderer<Outline>
{
    private int _outlineRTID, _blurredRTID, _temporaryRTID, _depthRTID, _idRTID;

    private int _RTWidth = 512;
    private int _RTHeight = 512;

    private Material _outlineMaterial;

    public override void Init()
    {
        _depthRTID = Shader.PropertyToID("_DepthRT");
        _outlineRTID = Shader.PropertyToID("_OutlineRT");
        _blurredRTID = Shader.PropertyToID("_BlurredRT");
        _temporaryRTID = Shader.PropertyToID("_TemporaryRT");
        _idRTID = Shader.PropertyToID("_idRT");

        _RTWidth = Screen.width;
        _RTHeight = Screen.height;

        _outlineMaterial = new Material(Shader.Find("Hidden/UnityOutline"));
    }

    public override void Render(PostProcessRenderContext context)
    {
        var cmd = context.command;
        cmd.BeginSample("OutlineRenderer");

        // initialization
        cmd.GetTemporaryRT(_depthRTID, _RTWidth, _RTHeight, 0, FilterMode.Bilinear, context.sourceFormat);
        cmd.SetRenderTarget(_depthRTID, BuiltinRenderTextureType.CurrentActive);
        cmd.ClearRenderTarget(false, true, Color.clear);

        // render selected objects into a mask buffer, with different colors for visible vs occluded ones 
        float id = 0f;
        foreach (var collection in settings.ObjectRenderers.value)
        {
            id += 0.25f;
            cmd.SetGlobalFloat("_ObjectId", id);

            foreach (var render in collection.Value)
            {
                for (var i = 0; i < render.sharedMaterials.Length; i++)
                {
                    cmd.DrawRenderer(render, _outlineMaterial, i, 1);
                    cmd.DrawRenderer(render, _outlineMaterial, i, 0);
                }
            }
        }

        // object ID edge dectection pass
        cmd.GetTemporaryRT(_idRTID, _RTWidth, _RTHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGB32);
        cmd.Blit(_depthRTID, _idRTID, _outlineMaterial, 3);

        // Blur
        int rtW = _RTWidth;
        int rtH = _RTHeight;
        var blurSize = settings.BlurSize.value;
        if (settings.Downsample)
        {
            blurSize /= 4;
            rtW >>= 1;
            rtH >>= 1;
        }

        cmd.GetTemporaryRT(_temporaryRTID, rtW, rtH, 0, FilterMode.Bilinear, RenderTextureFormat.ARGB32);
        cmd.GetTemporaryRT(_blurredRTID, rtW, rtH, 0, FilterMode.Bilinear, RenderTextureFormat.ARGB32);

        cmd.Blit(_idRTID, _blurredRTID);


        cmd.SetGlobalVector("_BlurDirection", new Vector2(blurSize, 0));
        cmd.Blit(_blurredRTID, _temporaryRTID, _outlineMaterial, 2);
        cmd.SetGlobalVector("_BlurDirection", new Vector2(0, blurSize));
        cmd.Blit(_temporaryRTID, _blurredRTID, _outlineMaterial, 2);


        // final overlay
        cmd.SetGlobalColor("_OutlineColor", settings.OutlineColor.value);

        cmd.BlitFullscreenTriangle(context.source, context.destination);
        cmd.Blit(_blurredRTID, context.destination, _outlineMaterial, 4);

        // release tempRTs
        cmd.ReleaseTemporaryRT(_blurredRTID);
        cmd.ReleaseTemporaryRT(_outlineRTID);
        cmd.ReleaseTemporaryRT(_temporaryRTID);
        cmd.ReleaseTemporaryRT(_depthRTID);

        cmd.EndSample("OutlineRenderer");
    }
}