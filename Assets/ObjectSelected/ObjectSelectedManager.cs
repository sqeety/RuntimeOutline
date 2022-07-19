using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
public class ObjectSelectedManager:MonoBehaviour
{
    private static bool isQuit = false;
    public PostProcessResources postProcessResources;
    private static ObjectSelectedManager mInstance;
    public static ObjectSelectedManager instance
    {
        get
        {
            if (mInstance == null)
            {
                GameObject obj = new GameObject("ObjectSelectedManager");
#if UNITY_EDITOR
                if(Application.isPlaying)
                    DontDestroyOnLoad(obj);
#else
                DontDestroyOnLoad(obj);
#endif
                
                mInstance = obj.AddComponent<ObjectSelectedManager>();
            }
            return mInstance;
        }
    }

    private void OnApplicationQuit()
    {
        isQuit = true;
    }

    [ColorUsage(true, true)] 
    public Color outlineColor = Color.yellow;
    [Range(0.0f, 10.0f)] 
    public float outlinePixelWidth = 2;
    public Dictionary<GameObject, List<Renderer>> wholeHighlightRenders = new(10);
    private CommandBuffer cb;
    private Material outlineMat;
    private Camera bufferCam;
    public Shader outlineShader;
    const CameraEvent cameraEvent = CameraEvent.AfterForwardOpaque;
    private int rt0 = Shader.PropertyToID("_RT0");
    private int rt1 = Shader.PropertyToID("_RT1");
    private int rt2 = Shader.PropertyToID("_ObjectSelectedCopy");
    private int outlineColorID = Shader.PropertyToID("_OutlineColor");
    private int outlinefadeID = Shader.PropertyToID("_OutlineFade");
    public float outlineFade = 1;
    private Material copyStdMaterial;

    public static void SelectedObject(GameObject target, bool highlight)
    {
        if (target == null || isQuit || mInstance == null) return;
        if (highlight)
        {
            mInstance.ModifyHighLightObject(target);
        }
        else
        {
            if (mInstance.wholeHighlightRenders.ContainsKey(target))
            {
                mInstance.wholeHighlightRenders.Remove(target);
            }
        }
    }

    public void ModifyHighLightObject(GameObject target)
    {
        if (!wholeHighlightRenders.TryGetValue(target, out var renderers))
        {
            renderers = new List<Renderer>(4);
            wholeHighlightRenders.Add(target, renderers);
        }
        target.GetComponentsInChildren(renderers);
    }

    private void Awake()
    {
        mInstance = this;
    }

    void OnEnable()
    {
        Camera.onPreRender += ApplyCommandBuffer;
        Camera.onPostRender += RemoveCommandBuffer;
    }

    void OnDisable()
    {
        Camera.onPreRender -= ApplyCommandBuffer;
        Camera.onPostRender -= RemoveCommandBuffer;
    }
    
    void ApplyCommandBuffer(Camera cam)
    {
#if UNITY_EDITOR
        // hack to avoid rendering in the inspector preview window
        if (cam.gameObject.name == "Preview Scene Camera")
            return;
#endif

        if (bufferCam != null)
        {
            if(bufferCam == cam)
                return;
            else
                RemoveCommandBuffer(cam);
        }

        CreateCommandBuffer(cam);
        if (cb == null)
            return;

        bufferCam = cam;
        bufferCam.AddCommandBuffer(cameraEvent, cb);
    }
    
    private void CreateCommandBuffer(Camera cam)
    {
        if (wholeHighlightRenders.Count == 0)
            return;
        
        if (outlineColor.a <= 1f/255f || outlinePixelWidth <= 0f)
            return;
        Profiler.BeginSample(nameof(CreateCommandBuffer));
        if (cb == null)
        {
            cb = new CommandBuffer();
            cb.name = "ObjectSelectedRenderer: " + gameObject.name;
        }
        else
        {
            cb.Clear();
        }

        if (outlineMat == null)
        {
            outlineMat = new Material(outlineShader);
        }

        // setup descriptor for silhouette render texture
        RenderTextureDescriptor rtd = new RenderTextureDescriptor() {
            dimension = TextureDimension.Tex2D,
            graphicsFormat = GraphicsFormat.R8_UNorm,

            width = cam.scaledPixelWidth,
            height = cam.scaledPixelHeight,

            msaaSamples = 1,

            sRGB = false,

            useMipMap = false,
            autoGenerateMips = false,
        };
        rtd.colorFormat = RenderTextureFormat.Default;
        rtd.depthStencilFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
        cb.GetTemporaryRT(rt2, rtd);
        cb.GetTemporaryRT(rt1, rtd);
        cb.GetTemporaryRT(rt0, rtd);
        if (copyStdMaterial == null)
        {
            copyStdMaterial = new Material(postProcessResources.shaders.copyStd);
        }
        cb.Blit(BuiltinRenderTextureType.CameraTarget, rt2, RuntimeUtilities.copyStdMaterial, 0);
        cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cb.ClearRenderTarget(false, true, Color.clear);
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);

        bool visible = false;
        float step = 1.0f / (wholeHighlightRenders.Count + 1.0f);
        float start = step;
        foreach (var highlightRender in wholeHighlightRenders)
        {
            cb.SetGlobalFloat("_ObjectId", start);
            start += step;
            foreach (var rd in highlightRender.Value)
            {
                if (GeometryUtility.TestPlanesAABB(planes, rd.bounds))
                {
                    visible = true;
                    var mesh = MeshFromRenderer(rd);
                    if (mesh != null)
                    {
                        if (rd.isPartOfStaticBatch)
                        {
                            cb.DrawRenderer(rd, outlineMat, 0, 1);
                            cb.DrawRenderer(rd, outlineMat, 0, 3);
                        }
                        else
                        {
                            var subMeshCount = mesh.subMeshCount;
                            for (int m = 0; m < subMeshCount; m++)
                            {
                                cb.DrawRenderer(rd, outlineMat, m, 1);
                                cb.DrawRenderer(rd, outlineMat, m, 3);
                            }
                        }
                    }
                }
            }
        }

        if (!visible)
        {
            Profiler.EndSample();
            return;
        }
        cb.SetRenderTarget(rt1);
        cb.Blit(BuiltinRenderTextureType.CameraTarget, rt1, outlineMat, 6);
        
        cb.SetRenderTarget(rt0);
        cb.SetGlobalVector("_BlurDirection", new Vector2(outlinePixelWidth,0));
        cb.Blit(rt1, rt0, outlineMat,5);
        
        cb.SetRenderTarget(rt1);
        cb.SetGlobalVector("_BlurDirection", new Vector2(0,outlinePixelWidth));
        cb.Blit(rt0, rt1, outlineMat, 5);
        
        cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cb.SetGlobalColor(outlineColorID, outlineColor);
        cb.SetGlobalFloat(outlinefadeID, outlineFade);
        cb.Blit(rt1,BuiltinRenderTextureType.CameraTarget, outlineMat, 4);

        cb.ReleaseTemporaryRT(rt2);
        cb.ReleaseTemporaryRT(rt1);
        cb.ReleaseTemporaryRT(rt0);
        Profiler.EndSample();
    }
    
    private Mesh MeshFromRenderer(Renderer r)
    {
        if (r is SkinnedMeshRenderer)
            return (r as SkinnedMeshRenderer).sharedMesh;
        if (r is MeshRenderer)
            return r.GetComponent<MeshFilter>().sharedMesh;

        return null;
    }
    
    void RemoveCommandBuffer(Camera cam)
    {
        if (bufferCam != null && cb != null)
        {
            bufferCam.RemoveCommandBuffer(cameraEvent, cb);
            bufferCam = null;
        }
    }
}