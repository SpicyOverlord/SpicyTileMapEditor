using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SpicyTilemapEditor
{
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshFilter))]
    [AddComponentMenu("")] // Disable attaching it to a gameobject
#if UNITY_2018_3_OR_NEWER
    [ExecuteAlways]
#else
    [ExecuteInEditMode] //NOTE: this is needed so OnDestroy is called and there is no memory leaks
#endif
    public partial class TilemapChunk : MonoBehaviour
    {
#region Public Properties
        public Tileset Tileset
        {
            get { return ParentTilemap.Tileset; }
        }

        public STETilemap ParentTilemap;
        /// <summary>
        /// The x position inside the parent tilemap
        /// </summary>
        public int GridPosX;
        /// <summary>
        /// The y position inside the parent tilemap
        /// </summary>
        public int GridPosY;

        public int GridWidth { get { return m_width; } }
        public int GridHeight { get { return m_height; } }

        public int SortingLayerID
        {
            get { return m_meshRenderer.sortingLayerID; }
            set { m_meshRenderer.sortingLayerID = value; }
        }

        public string SortingLayerName
        {
            get { return m_meshRenderer.sortingLayerName; }
            set { m_meshRenderer.sortingLayerName = value; }
        }

        public int OrderInLayer
        {
            get { return m_meshRenderer.sortingOrder; }
            set { m_meshRenderer.sortingOrder = value; }
        }

        public MeshFilter MeshFilter { get { return m_meshFilter; } }

        public Vector2 CellSize { get { return ParentTilemap.CellSize; } }
        /// <summary>
        /// Stretch the size of the tile UV the pixels indicated in this value. This trick help to fix pixel artifacts.
        /// Most of the time a value of 0.5 pixels will be fine, but in case of a big zoom-out level, a higher value will be necessary
        /// </summary>
        public float InnerPadding { get { return ParentTilemap.InnerPadding; } }
#endregion

#region Private Fields

        [SerializeField, HideInInspector]
        private int m_width = -1;
        [SerializeField, HideInInspector]
        private int m_height = -1;
        [SerializeField, HideInInspector]
        private List<uint> m_tileDataList = new List<uint>();        
        [SerializeField, HideInInspector]
        private List<TileColor32> m_tileColorList = null;

        //+++ MeshCollider
        [SerializeField, HideInInspector]
        private MeshCollider m_meshCollider;
        private static List<Vector3> s_meshCollVertices;
        private static List<int> s_meshCollTriangles;
        //---

        //+++ 2D Edge colliders
        [SerializeField]
        private bool m_has2DColliders;
        //---

        //+++ Renderer
        [SerializeField, HideInInspector]
        private MeshFilter m_meshFilter;
        [SerializeField, HideInInspector]
        private MeshRenderer m_meshRenderer;
        //---

        [SerializeField, HideInInspector]
        private List<TileObjData> m_tileObjList = new List<TileObjData>();
        private List<GameObject> m_tileObjToBeRemoved = new List<GameObject>();

        private static List<Vector3> s_vertices;
        private List<Vector2> m_uv; //NOTE: this is the only one not static because it's needed to update the animated tiles
        private static List<int> s_triangles;
        private static List<Color32> s_colors32 = null;        

        struct AnimTileData
        {
            public int VertexIdx;
            public int SubTileIdx;
            public byte flags;
            public IBrush Brush;
        }
        private List<AnimTileData> m_animatedTiles = new List<AnimTileData>();
#endregion

#region Monobehaviour Methods

        private MaterialPropertyBlock m_matPropBlock;
        void UpdateMaterialPropertyBlock()
        {
            if(m_matPropBlock == null)
                m_matPropBlock = new MaterialPropertyBlock();
            m_meshRenderer.GetPropertyBlock(m_matPropBlock);
#if UNITY_EDITOR
            // Apply UnselectedColorMultiplier
            TilemapGroup selectedTilemapGroup;
            if (
                !Application.isPlaying &&
                // Check if there is a parent tilemap group and this tilemap is not the selected tilemap in the tilemap group
                ParentTilemap.ParentTilemapGroup && 
                ParentTilemap.ParentTilemapGroup.SelectedTilemap != ParentTilemap &&
                // Check if the tilemap group or any of its children is selected
                Selection.activeGameObject &&
                (Selection.activeGameObject == ParentTilemap.ParentTilemapGroup.gameObject ||
                ((selectedTilemapGroup = Selection.activeGameObject.GetComponentInParent<TilemapGroup>()) && selectedTilemapGroup == ParentTilemap.ParentTilemapGroup) &&
                // Exception: the selected object is parent of the tilemap but it's not a tilemap group (ex: grouping tilemaps under a dummy gameobject)
                !(ParentTilemap.transform.IsChildOf(Selection.activeGameObject.transform) && !Selection.activeGameObject.GetComponent<TilemapGroup>())
                )
            )
            {
                m_matPropBlock.SetColor("_Color", ParentTilemap.TintColor * ParentTilemap.ParentTilemapGroup.UnselectedColorMultiplier);
            }
            else
#endif
            {
                m_matPropBlock.SetColor("_Color", ParentTilemap.TintColor);
            }
            if (Tileset && Tileset.AtlasTexture != null)
                m_matPropBlock.SetTexture("_MainTex", Tileset.AtlasTexture);
            //else //TODO: find a way to set a null texture or pink texture
              //  m_matPropBlock.SetTexture("_MainTex", default(Texture));
            m_meshRenderer.SetPropertyBlock(m_matPropBlock);
        }

        static Dictionary<Material, Material[]> s_materialsLookup = new Dictionary<Material, Material[]>();
        static Shader s_spritesStencilDefault = null;
        static Shader s_spritesStencilDiffuse = null;
        private static System.Func<Material, int, Material[]> _FindSharedMaterialArray =
        delegate (Material material, int materialHash)
        {
            Material[] matArr;
            if (!s_materialsLookup.TryGetValue(material, out matArr))
            {
                Shader stencilShader = null;
                if (material.shader.name == "Sprites/Default")
                {
                    stencilShader = s_spritesStencilDefault ?? (s_spritesStencilDefault = Shader.Find("Sprites/Stencil-Default"));                    
                    Debug.Assert(stencilShader, "Could not find Sprites/Stencil-Default shader!");
                }
                else if (material.shader.name == "Sprites/Diffuse")
                {
                    stencilShader = s_spritesStencilDiffuse ?? (s_spritesStencilDiffuse = Shader.Find("Sprites/Stencil-Diffuse"));
                    Debug.Assert(stencilShader, "Could not find Sprites/Stencil-Diffuse shader!");
                }
                matArr = new Material[6];
                matArr[0] = material;
                // Pixel Snap = false //
                if (stencilShader)
                {
                    matArr[1] = new Material(stencilShader);
                    matArr[1].CopyPropertiesFromMaterial(material);
                    matArr[1].SetFloat("_Stencil", 1f);
                    matArr[2] = new Material(matArr[1]);
                    matArr[2].CopyPropertiesFromMaterial(matArr[1]); // NOTE: this is needed because the properties are not copied in the constructor
                    matArr[1].SetFloat("_StencilComp", (int)CompareFunction.LessEqual);// SpriteMaskInteraction = VisibleInsideMask
                    matArr[2].SetFloat("_StencilComp", (int)CompareFunction.Greater);// SpriteMaskInteraction = VisibleOutsideMask
                    matArr[1].hideFlags = HideFlags.DontSave;
                    matArr[2].hideFlags = HideFlags.DontSave;                    
                    matArr[1].name += "_VisibleInsideMask";
                    matArr[2].name += "_VisibleOutsideMask";
                }
                else
                {
                    matArr[1] = material;
                    matArr[2] = material;
                }
                ////////////////////////

                // Pixel Snap = true //
                for (int i = 3, j = 0; i < 6; ++i, ++j)
                {
                    var matCopy = new Material(matArr[j]);
                    matCopy.CopyPropertiesFromMaterial(matArr[j]);
                    matArr[i] = matCopy;                    
                    matCopy.name += "_pixelSnap";
                    matCopy.hideFlags = HideFlags.DontSave;
                    matCopy.EnableKeyword("PIXELSNAP_ON");
                    matCopy.SetFloat("PixelSnap", 1f);
                }
                ///////////////////////

                s_materialsLookup[material] = matArr;
            }
            return matArr;
        };

        private static System.Func<Material, int, Material> _FindMaterial =
        delegate (Material material, int materialHash)
        {
            if (materialHash == 0)
                return material;
            Material[] materialArr = _FindSharedMaterialArray(material, materialHash);
            //Debug.Log("Debug _FindMaterial " + materialArr[materialHash].name);
            return materialArr[materialHash];
        };

        // Depending on some properties, find a copy of the material with support for thouse properties
        int m_materialHash = 0; // all combinations with pixelSnap + MaskInteraction
        Shader m_savedMaterialShader;
        public void UpdateMeshMaterial()
        {
            int savedHash = m_materialHash;
            m_materialHash = ParentTilemap.PixelSnap?
                3 + (int)ParentTilemap.MaskInteraction
                :
                (int)ParentTilemap.MaskInteraction;


            // for performance, only if it changed the hash is updated according to material support for each property
            if(m_materialHash != savedHash)
            {
                if (m_materialHash >= 3 && !ParentTilemap.Material.HasProperty("PixelSnap"))
                    m_materialHash -= 3; // disable pixel snap
                if (
                    (!ParentTilemap.Material.HasProperty("_StencilComp") || !ParentTilemap.Material.HasProperty("_Stencil")) &&
                    //exception: these shaders will be switched to stencil equivalent in _FindMaterial
                    (ParentTilemap.Material.shader.name != "Sprites/Default" && ParentTilemap.Material.shader.name != "Sprites/Diffuse"))
                {
                    m_materialHash = m_materialHash >= 3 ? 3 : 0; // disable sprite masking
                }
            }

            if(!m_savedMaterialShader)
                m_savedMaterialShader = ParentTilemap.Material.shader;
            if (m_savedMaterialShader != ParentTilemap.Material.shader)
            {
                //Debug.LogFormat("Shader has changed from {0} to {1}", m_savedMaterialShader.name, ParentTilemap.Material.shader);                
                m_savedMaterialShader = ParentTilemap.Material.shader;
                //Debug.Log("Clear " + ParentTilemap.Material.name + " from cache");
                s_materialsLookup.Remove(ParentTilemap.Material);
                m_meshRenderer.sharedMaterial = _FindMaterial(ParentTilemap.Material, m_materialHash);
            }

            if (m_materialHash != savedHash || !m_meshRenderer.sharedMaterial)
            {
                m_meshRenderer.sharedMaterial = _FindMaterial(ParentTilemap.Material, m_materialHash);
            }

            //Fix: Unity Editor Shader Properties overridden
            if( Application.isEditor && ParentTilemap.MaskInteraction != SpriteMaskInteraction.None)
            {
                int comp = ParentTilemap.MaskInteraction == SpriteMaskInteraction.VisibleInsideMask ?
                    (int)CompareFunction.LessEqual
                    :
                    (int)CompareFunction.Greater;
                m_meshRenderer.sharedMaterial.SetFloat("_StencilComp", comp);
            }
        }

        void LateUpdate()
        {
            if (!ParentTilemap && !transform.parent) //NOTE: it happens only once, but it could happen a chunk is left orphan
            {
                DestroyImmediate(gameObject);
                return;
            }

            if (!ParentTilemap.Tileset)
                return;

            UpdateMeshMaterial();
            UpdateMaterialPropertyBlock();

            if (m_animatedTiles.Count > 0) //TODO: add fps attribute to update animated tiles when necessary
            {
                for (int i = 0; i < m_animatedTiles.Count; ++i)
                {
                    AnimTileData animTileData = m_animatedTiles[i];
                    Vector2[] uvs = animTileData.Brush.GetAnimUVWithFlags(InnerPadding, i, (uint)animTileData.flags << 28);
                    if (animTileData.SubTileIdx >= 0)
                    {
                        for (int j = 0; j < 4; ++j)
                        {
                            if (j == animTileData.SubTileIdx)
                                m_uv[animTileData.VertexIdx + j] = uvs[j];
                            else
                                m_uv[animTileData.VertexIdx + j] = (uvs[j] + uvs[animTileData.SubTileIdx]) / 2f;
                        }
                    }
                    else
                    {
                        m_uv[animTileData.VertexIdx + 0] = uvs[0];
                        m_uv[animTileData.VertexIdx + 1] = uvs[1];
                        m_uv[animTileData.VertexIdx + 2] = uvs[2];
                        m_uv[animTileData.VertexIdx + 3] = uvs[3];
                    }
                }                

                if (m_meshFilter.sharedMesh) 
#if UNITY_5_0 || UNITY_5_1
                    m_meshFilter.sharedMesh.uv = m_uv.ToArray();
#else
                    m_meshFilter.sharedMesh.SetUVs(0, m_uv);
#endif
            }
        }

        // NOTE: OnDestroy is not called in editor without [ExecuteInEditMode]
        void OnDestroy()
        {
            //avoid memory leak
            DestroyMeshIfNeeded();
            DestroyColliderMeshIfNeeded();
        }

        // This is needed to refresh tilechunks after undo / redo actions
        static bool s_isOnValidate = false; // fix issue when destroying unused resources from the invalidate call
#if UNITY_EDITOR
        void OnValidate()
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.ExecuteCommand && (e.commandName == "Duplicate" || e.commandName == "Paste"))
            {
                _DoDuplicate();
            }

            EditorApplication.update -= DoLateOnValidate;
            EditorApplication.update += DoLateOnValidate;            
        }

        private void DoLateOnValidate()
        {
            EditorApplication.update -= DoLateOnValidate;

            if (!this || !ParentTilemap)
                return;
            //Debug.Log("DoLateOnValidate " + ParentTilemap.name + "/" + name, gameObject);

            // fix prefab preview
            if (EditorCompatibilityUtils.IsPrefab(gameObject) && !gameObject.scene.IsValid())
            {
#if !UNITY_2018_3_OR_NEWER // disabled prefab preview for performance issue with new prefab workflow
                m_needsRebuildMesh = true;
                UpdateMesh(true);
#endif
            }
            else
            {
                m_needsRebuildMesh = true;
                m_needsRebuildColliders = true;
                if (ParentTilemap) //NOTE: this is null sometimes in Unity 2017.2.0b4. It happens when the brush is changed, so maybe it's related with the brush but transform.parent is null.
                    ParentTilemap.UpdateMeshImmediate();
            }
        }
#endif

        private void _DoDuplicate()
        {
            // When copying a tilemap, the sharedMesh will be the same as the copied tilemap, so it has to be created a new one
            m_meshFilter.sharedMesh = null; // NOTE: if not nulled before the new Mesh, the previous mesh will be destroyed
            m_meshFilter.sharedMesh = new Mesh();
            m_meshFilter.sharedMesh.hideFlags = HideFlags.DontSave;
            m_meshFilter.sharedMesh.name = ParentTilemap.name + "_Copy_mesh";
            m_needsRebuildMesh = true;
            if (m_meshCollider != null)
            {
                m_meshCollider.sharedMesh = null; // NOTE: if not nulled before the new Mesh, the previous mesh will be destroyed
                m_meshCollider.sharedMesh = new Mesh();
                m_meshCollider.sharedMesh.hideFlags = HideFlags.DontSave;
                m_meshCollider.sharedMesh.name = ParentTilemap.name + "_Copy_collmesh";
            }
            m_needsRebuildColliders = true;
            //---
        }

        void Awake()
        {
            //+++ fix when a tilemap is instantiated from a prefab, the Mesh is shared between all instances
            if (m_meshFilter) m_meshFilter.sharedMesh = null;
            if (m_meshCollider) m_meshCollider.sharedMesh = null;
            //---
        }

        void OnEnable()
        {
            if (ParentTilemap == null)
            {
                ParentTilemap = GetComponentInParent<STETilemap>();
            }

#if UNITY_EDITOR
            if (m_meshRenderer != null)
            {
#if UNITY_5_5_OR_NEWER
                EditorUtility.SetSelectedRenderState(m_meshRenderer, EditorSelectedRenderState.Hidden);
#else
                EditorUtility.SetSelectedWireframeHidden(m_meshRenderer, true);
#endif
            }
#endif
            m_meshRenderer = GetComponent<MeshRenderer>();
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshCollider = GetComponent<MeshCollider>();

            //Fix strange case where two materials were being used, breaking lighting
            if (m_meshRenderer.sharedMaterials.Length > 1)
            {
                Debug.LogWarning("Fixing TileChunk with multiple materials!");
                m_meshRenderer.sharedMaterials = new Material[] { m_meshRenderer.sharedMaterials[0] };
            }
            //

            if (m_tileDataList == null || m_tileDataList.Count != m_width * m_height)
            {
                SetDimensions(m_width, m_height);
            }

            // if not playing, this will be done later by OnValidate
            if (Application.isPlaying && IsInitialized()) //NOTE: && IsInitialized was added to avoid calling UpdateMesh when adding this component and GridPos was set
            {
                // Refresh only if Mesh is null (this happens if hideFlags == DontSave)
                m_needsRebuildMesh = m_meshFilter.sharedMesh == null;
                m_needsRebuildColliders = ParentTilemap.ColliderType == EColliderType._3D && (m_meshCollider == null || m_meshCollider.sharedMesh == null);
                UpdateMesh();
                UpdateColliders();
            }
        }

        public bool IsInitialized()
        {
            return m_width > 0 && m_height > 0;
        }

        public void Reset()
        {
            SetDimensions(m_width, m_height);           

#if UNITY_EDITOR
            if (m_meshRenderer != null)
            {
#if UNITY_5_5_OR_NEWER
                EditorUtility.SetSelectedRenderState(m_meshRenderer, EditorSelectedRenderState.Hidden);
#else
                EditorUtility.SetSelectedWireframeHidden(m_meshRenderer, true);
#endif
            }
#endif
            m_needsRebuildMesh = true;
            m_needsRebuildColliders = true;
        }
#endregion

#region Public Methods

        /// <summary>
        /// This fix should be called on next update after updating the MeshCollider (sharedMesh, convex or isTrigger property). 
        /// For some reason, if this is not called, the OnCollision event will return a collision 
        /// data with empty contacts points array until this is called for all colliders not touching the tilechunk collider when it was modified
        /// </summary>
        public void ApplyContactsEmptyFix()
        {
            if (m_meshCollider) m_meshCollider.convex = m_meshCollider.convex;
        }

        public void UpdateRendererProperties()
        {
            m_meshRenderer.shadowCastingMode = ParentTilemap.ChunkRendererProperties.castShadows;
            m_meshRenderer.receiveShadows = ParentTilemap.ChunkRendererProperties.receiveShadows;
#if UNITY_5_4_OR_NEWER
            m_meshRenderer.lightProbeUsage = ParentTilemap.ChunkRendererProperties.useLightProbes;
#else
            m_meshRenderer.useLightProbes = ParentTilemap.ChunkRendererProperties.useLightProbes;
#endif
            m_meshRenderer.reflectionProbeUsage = ParentTilemap.ChunkRendererProperties.reflectionProbeUsage;
            m_meshRenderer.probeAnchor = ParentTilemap.ChunkRendererProperties.anchorOverride;
        }

        public void DrawColliders()
        {
            if (ParentTilemap.ColliderType == EColliderType._3D)
            {
                if (m_meshCollider != null && m_meshCollider.sharedMesh != null && m_meshCollider.sharedMesh.normals.Length > 0f)
                {
                    Gizmos.color = EditorGlobalSettings.TilemapColliderColor;
                    Gizmos.DrawWireMesh(m_meshCollider.sharedMesh, transform.position, transform.rotation, transform.lossyScale);
                    Gizmos.color = Color.white;
                }
            }
            else if(ParentTilemap.ColliderType == EColliderType._2D)
            {
                Gizmos.color = EditorGlobalSettings.TilemapColliderColor;
                Gizmos.matrix = gameObject.transform.localToWorldMatrix;
                Collider2D[] edgeColliders = GetComponents<Collider2D>();
                for(int i = 0; i < edgeColliders.Length; ++i)
                {
                    Collider2D collider2D = edgeColliders[i];
                    if (collider2D.enabled)
                    {
                        int size = 0;
                        Vector2[] points = null;
                        if (collider2D is EdgeCollider2D)
                        {
                            points = ((EdgeCollider2D)collider2D).points;
                            size = (points.Length - 1);
                            _Draw2DCollider(points, size);
                        }
                        else if(collider2D is PolygonCollider2D)
                        {
                            for (int pathIdx = 0,
                                pathCount = ((PolygonCollider2D)collider2D).pathCount; 
                                pathIdx < pathCount; pathIdx++)
                            {
                                points = ((PolygonCollider2D)collider2D).GetPath(pathIdx);
                                size = points.Length;
                                _Draw2DCollider(points, size);
                            }
                        }                        
                    }
                }
                Gizmos.matrix = Matrix4x4.identity;
                Gizmos.color = Color.white;
            }
        }

        private void _Draw2DCollider(Vector2[] points, int size)
        {
            for (int j = 0; j < size; ++j)
            {
                int nextIdx = j + 1;
                if (nextIdx == points.Length)
                    nextIdx = 0;
                Gizmos.DrawLine(points[j], points[nextIdx]);
                //Draw normals
                if (ParentTilemap.ShowColliderNormals)
                {
                    Vector2 s0 = points[j];
                    Vector2 s1 = points[nextIdx];
                    Vector3 normPos = (s0 + s1) / 2f;
                    Gizmos.DrawLine(normPos, normPos + Vector3.Cross(s1 - s0, -Vector3.forward).normalized * ParentTilemap.CellSize.y * 0.05f);
                }
            }
        }

        public Bounds GetBounds()
        {
            Bounds bounds = MeshFilter.sharedMesh? MeshFilter.sharedMesh.bounds : default(Bounds);
            if (bounds == default(Bounds))
            {
                Vector3 vMinMax = Vector2.Scale(new Vector2(GridPosX < 0? GridWidth : 0f, GridPosY < 0? GridHeight : 0f), CellSize);
                bounds.SetMinMax( vMinMax, vMinMax);
            }
            for (int i = 0; i < m_tileObjList.Count; ++i )
            {
                int locGx = m_tileObjList[i].tilePos % GridWidth;
                if (GridPosX >= 0) locGx++;
                int locGy = m_tileObjList[i].tilePos / GridWidth;
                if (GridPosY >= 0) locGy++;
                Vector2 gridPos = Vector2.Scale( new Vector2(locGx, locGy), CellSize);
                bounds.Encapsulate(gridPos);
            }
            return bounds;
        }

        public void SetDimensions(int width, int height)
        {
            int size = width * height;
            if (size > 0 && size * 4 < 65000) //NOTE: 65000 is the current maximum vertex allowed per mesh and each tile has 4 vertex
            {
                m_width = width;
                m_height = height;
                m_tileDataList = Enumerable.Repeat(Tileset.k_TileData_Empty, size).ToList();
            }
            else
            {
                Debug.LogWarning("Invalid parameters!");
            }
        }

        public void SetTileData(Vector2 vLocalPos, uint tileData)
        {
            SetTileData((int)(vLocalPos.x / CellSize.x), (int)(vLocalPos.y / CellSize.y), tileData);
        }

        public void SetTileData(int locGridX, int locGridY, uint tileData)
        {
            if (locGridX >= 0 && locGridX < m_width && locGridY >= 0 && locGridY < m_height)
            {
                int tileIdx = locGridY * m_width + locGridX;

                int tileId = (int)(tileData & Tileset.k_TileDataMask_TileId);
                Tile tile = Tileset.GetTile(tileId);

                int prevTileId = (int)(m_tileDataList[tileIdx] & Tileset.k_TileDataMask_TileId);
                Tile prevTile = Tileset.GetTile(prevTileId);                             

                int brushId = Tileset.GetBrushIdFromTileData(tileData);
                int prevBrushId = Tileset.GetBrushIdFromTileData(m_tileDataList[tileIdx]);

                if (brushId != prevBrushId
                    || brushId == 0) //NOTE: because of the autotiling mode, neighbour tiles could be affected by this change, even if the tile is not a brush
                {
                    if (!s_currUpdatedTilechunk) // avoid this is chunks is being Updated from FillMeshData
                    { 
                        // Refresh Neighbors ( and itself if needed )
                        for (int yf = -1; yf <= 1; ++yf)
                        {
                            for (int xf = -1; xf <= 1; ++xf)
                            {
                                if ((xf | yf) == 0)
                                {
                                    if (brushId > 0)
                                    {
                                        // Refresh itself
                                        tileData = (tileData & ~Tileset.k_TileFlag_Updated);
                                    }
                                }
                                else
                                {
                                    int gx = (locGridX + xf);
                                    int gy = (locGridY + yf);
                                    int idx = gy * m_width + gx;
                                    bool isInsideChunk = (gx >= 0 && gx < m_width && gy >= 0 && gy < m_height);
                                    uint neighborTileData = isInsideChunk ? m_tileDataList[idx] : ParentTilemap.GetTileData(GridPosX + locGridX + xf, GridPosY + locGridY + yf);
                                    int neighborBrushId = (int)((neighborTileData & Tileset.k_TileDataMask_BrushId) >> 16);
                                    TilesetBrush neighborBrush = ParentTilemap.Tileset.FindBrush(neighborBrushId);
                                    if (neighborBrush != null &&
                                        (neighborBrush.AutotileWith(ParentTilemap.Tileset, neighborBrushId, tileData) || neighborBrush.AutotileWith(ParentTilemap.Tileset, neighborBrushId, m_tileDataList[tileIdx])))
                                    {
                                        neighborTileData = (neighborTileData & ~Tileset.k_TileFlag_Updated); // force a refresh
                                        if (isInsideChunk)
                                        {
                                            m_tileDataList[idx] = neighborTileData;
                                        }
                                        else
                                        {
                                            ParentTilemap.SetTileData(GridPosX + gx, GridPosY + gy, neighborTileData);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if(brushId > 0)
                {
                    // Refresh itself
                    tileData = (tileData & ~Tileset.k_TileFlag_Updated);
                }

                m_needsRebuildMesh |= (m_tileDataList[tileIdx] != tileData) // OR there is a tile not empty and tileId is -1
                    || (tileData != Tileset.k_TileData_Empty) &&  (tileData & Tileset.k_TileDataMask_TileId) == Tileset.k_TileId_Empty;

                m_needsRebuildColliders |= m_needsRebuildMesh &&
                (
                    (prevBrushId > 0) || (brushId > 0) // there is a brush (a brush could change the collider data later)
                    || (tile != null && tile.collData.type != eTileCollider.None) || (prevTile != null && prevTile.collData.type != eTileCollider.None) // prev. or new tile has colliders
                );

                if (ParentTilemap.ColliderType != EColliderType.None && m_needsRebuildColliders)
                {
                    // Refresh Neighbors tilechunk colliders, to make the collider autotiling
                    // Only if neighbor is outside this tilechunk
                    for (int yf = -1; yf <= 1; ++yf)
                    {
                        for (int xf = -1; xf <= 1; ++xf)
                        {
                            if ((xf | yf) != 0) // skip this tile position xf = yf = 0
                            {
                                int gx = (locGridX + xf);
                                int gy = (locGridY + yf);
                                bool isInsideChunk = (gx >= 0 && gx < m_width && gy >= 0 && gy < m_height);
                                if (!isInsideChunk)
                                {
                                    ParentTilemap.InvalidateChunkAt(GridPosX + gx, GridPosY + gy, false, true);
                                }
                            }
                        }
                    }
                }

                // Update tile data
                m_tileDataList[tileIdx] = tileData;

                if (!STETilemap.DisableTilePrefabCreation)
                {
                    // Create tile Objects
                    if (tile != null && tile.prefabData.prefab != null)
                        CreateTileObject(tileIdx, tile.prefabData);
                    else
                        DestroyTileObject(tileIdx);
                }

                TilesetBrush brush = ParentTilemap.Tileset.FindBrush(brushId);
                if (brushId != prevBrushId)
                {
                    TilesetBrush prevBrush = ParentTilemap.Tileset.FindBrush(prevBrushId);
                    if (prevBrush != null)
                    {
                        prevBrush.OnErase(this, locGridX, locGridY, tileData, prevBrushId);
                    }
                }
                if (brush != null)
                {
                    tileData = brush.OnPaint(this, locGridX, locGridY, tileData);
                }
            }
        }

        public uint GetTileData(Vector2 vLocalPos)
        {
            return GetTileData((int)(vLocalPos.x / CellSize.x), (int)(vLocalPos.y / CellSize.y));
        }

        public uint GetTileData(int locGridX, int locGridY)
        {
            if (locGridX < 0 || locGridX >= m_width || 
                locGridY < 0 || locGridY >= m_height)
                return Tileset.k_TileData_Empty;
            
            int tileIdx = locGridY * m_width + locGridX;
            return m_tileDataList[tileIdx];

        }

        public void RefreshTile(int locGridX, int locGridY)
        {
            if (locGridX < 0 || locGridX >= m_width || locGridY < 0 || locGridY >= m_height) return;
            int tileIdx = locGridY * m_width + locGridX;
            m_tileDataList[tileIdx] &= ~Tileset.k_TileFlag_Updated;
        }

        public GameObject GetTileObject(int locGridX, int locGridY)
        {
            TileObjData tileObjData = null;
            if (locGridX >= 0 && locGridX < m_width && locGridY >= 0 && locGridY < m_height)
            {
                int tileIdx = locGridY * m_width + locGridX;
                tileObjData = FindTileObjDataByTileIdx(tileIdx);
            }
            return tileObjData != null ? tileObjData.obj : null;
        }
#endregion
    }

    [System.Serializable]
    public struct TileColor32
    {
        public Color32 c0;
        public Color32 c1;
        public Color32 c2;
        public Color32 c3;
        public bool singleColor;
        public static bool isValidColor(Color32 c) { return (c.r | c.g | c.b | c.a) == 0; }
        public static TileColor32 white { get { return s_white; } }
        public static TileColor32 invalid { get { return s_white; } }
        private static TileColor32 s_white = new TileColor32(Color.white, Color.white, Color.white, Color.white);
        public TileColor32(Color32 color)
        {
            this.c0 = this.c1 = this.c2 = this.c3 = color;
            singleColor = true;
        }
        public TileColor32(Color32 c0, Color32 c1, Color32 c2, Color32 c3)
        {
            this.c0 = c0;
            this.c1 = c1;
            this.c2 = c2;
            this.c3 = c3;
            singleColor = false;
        }

        public override string ToString()
        {
            return singleColor? this.c0.ToString() : string.Format("{{c0:{0}, c1:{1}, c2:{2}, c3:{3}}}", c0, c1, c2, c3);
        }

        public static TileColor32 BlendColors(TileColor32 tileColorA, TileColor32 tileColorB, EBlendMode blendMode)
        {
            if (tileColorA.singleColor && tileColorB.singleColor)
                return new TileColor32(BlendColor(tileColorA.c0, tileColorB.c0, blendMode));
            else
                return new TileColor32(BlendColor(tileColorA.c0, tileColorB.c0, blendMode), BlendColor(tileColorA.c1, tileColorB.c1, blendMode), BlendColor(tileColorA.c2, tileColorB.c2, blendMode), BlendColor(tileColorA.c3, tileColorB.c3, blendMode));
        }

        public static Color32 BlendColor(Color32 colorA, Color32 colorB, EBlendMode blendMode)
        {
            if ((colorB.r | colorB.g | colorB.b | colorB.a) == 0) //NOTE: if all the values are 0, this color blend is skipped
                return colorA;
            switch (blendMode)
            {
                case EBlendMode.None:
                    return colorB;
                case EBlendMode.AlphaBlending:
                    {
                        if (colorB.a == 255)
                            return colorB;
                        /*Ref: https://en.wikipedia.org/wiki/Alpha_compositing
                         * Not working properly if ColorA.a is not 255
                        int outA = (colorB.a + (colorA.a * (255 - colorB.a)) / 255);
                        int invAlpha = 255 - colorB.a;
                        if (outA > 0f)
                        {
                            colorA.r = (byte)((colorB.r * colorB.a + colorA.r * colorA.a * invAlpha / 255) / outA);
                            colorA.g = (byte)((colorB.g * colorB.a + colorA.g * colorA.a * invAlpha / 255) / outA);
                            colorA.b = (byte)((colorB.b * colorB.a + colorA.b * colorA.a * invAlpha / 255) / outA);
                            if(colorA.r + colorA.g + colorA.b == 0)
                                Debug.Log("outA: " + outA + " cA " + colorA + " cB " + colorB);
                        }
                        colorA.a = (byte)outA;
                        return colorA;
                        */
                        int invAlpha = 255 - colorB.a;
                        byte r = (byte)((colorA.r * invAlpha + colorB.r * colorB.a) / 255);
                        byte g = (byte)((colorA.g * invAlpha + colorB.g * colorB.a) / 255);
                        byte b = (byte)((colorA.b * invAlpha + colorB.b * colorB.a) / 255);
                        return new Color32(r, g, b, colorA.a);
                    }
                case EBlendMode.Additive:
                    {
                        byte r = (byte)Mathf.Min(255, colorA.r + colorB.r * colorB.a / 255);
                        byte g = (byte)Mathf.Min(255, colorA.g + colorB.g * colorB.a / 255);
                        byte b = (byte)Mathf.Min(255, colorA.b + colorB.b * colorB.a / 255);
                        return new Color32(r, g, b, colorA.a);
                    }
                case EBlendMode.Subtractive:
                    {
                        byte r = (byte)Mathf.Max(0, colorA.r - colorB.r * colorB.a / 255);
                        byte g = (byte)Mathf.Max(0, colorA.g - colorB.g * colorB.a / 255);
                        byte b = (byte)Mathf.Max(0, colorA.b - colorB.b * colorB.a / 255);
                        return new Color32(r, g, b, colorA.a);
                    }
                case EBlendMode.Multiply:
                    {
                        int invAlpha = 255 - colorB.a;
                        byte r = (byte)(colorA.r * invAlpha / 255 + colorA.r * colorB.r * colorB.a / 65025); //65025 = 255 * 255
                        byte g = (byte)(colorA.g * invAlpha / 255 + colorA.g * colorB.g * colorB.a / 65025); //65025 = 255 * 255
                        byte b = (byte)(colorA.b * invAlpha / 255 + colorA.b * colorB.b * colorB.a / 65025); //65025 = 255 * 255
                        return new Color32(r, g, b, colorA.a);
                    }
                case EBlendMode.Divide:
                    {
                        int invAlpha = 255 - colorB.a;
                        byte r = (byte)(colorA.r * invAlpha / 255 + (colorA.r > colorB.r? colorB.a : colorA.r * colorB.a / (colorB.r + 1)));
                        byte g = (byte)(colorA.g * invAlpha / 255 + (colorA.g > colorB.g ? colorB.a : colorA.g * colorB.a / (colorB.g + 1)));
                        byte b = (byte)(colorA.b * invAlpha / 255 + (colorA.b > colorB.b ? colorB.a : colorA.b * colorB.a / (colorB.b + 1)));
                        return new Color32(r, g, b, colorA.a);
                    }
                default:
                    return colorB;
            }
        }
    }
}
