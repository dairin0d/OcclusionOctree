// An experiment with octree rasterization, based on the ideas outlined in
// https://ecstatica2.livejournal.com/43748.html (the article is in Russian)

using UnityEngine;
using System.Collections.Generic;

// TODO:
// comment, cleanup, share?

public partial class VoxelRenderer : MonoBehaviour {
	[Range(8, 22)] // 22 is sane max, since float has only 23 bits precision
	public int DepthBufferBits = 22;

	public TextureFormat format = TextureFormat.ARGB32;

	[Range(1, 8)]
	public int ResolutionFactor = 1;

	public Shader shader;
	Material material;
	Texture2D texture;

	static Color32[] color_buffer;
	static float[] depth_buffer;
	//static int[] depth_buffer;

	Camera cam;
	Transform tfm;

	Dictionary<TextAsset, VoxelObject.Octree> octree_datas;

	public List<VoxelObject> voxel_objs;

	System.Diagnostics.Stopwatch stopwatch;

	public static VoxelRenderer instance = null;

	void Awake() {
		instance = this;
		octree_datas = new Dictionary<TextAsset, VoxelObject.Octree>();
		voxel_objs = new List<VoxelObject>();
	}

	void OnDestroy() {
		instance = null;
	}

	public void AddVoxelObject(VoxelObject voxel_obj) {
		voxel_objs.Add(voxel_obj);
		if ((voxel_obj.octree == null) && voxel_obj.octree_data) {
			VoxelObject.Octree octree;
			if (octree_datas.TryGetValue(voxel_obj.octree_data, out octree)) {
				voxel_obj.octree = octree;
			} else {
				octree = voxel_obj.ReadOctree();
				octree_datas.Add(voxel_obj.octree_data, octree);
			}
		}
	}

	public void RemoveVoxelObject(VoxelObject voxel_obj) {
		if (!voxel_obj) return;
		voxel_objs.Remove(voxel_obj);
	}

	void ClearRemovedObjects() {
		List<VoxelObject> objs_to_remove = null;
		foreach (var voxel_obj in voxel_objs) {
			if (!voxel_obj) {
				if (objs_to_remove == null) objs_to_remove = new List<VoxelObject>();
				objs_to_remove.Add(voxel_obj);
			}
		}
		if (objs_to_remove != null) {
			foreach (var voxel_obj in objs_to_remove) {
				voxel_objs.Remove(voxel_obj);
			}
		}
	}

	void Start() {
		tfm = this.transform;

		cam = GetComponent<Camera>();
		material = new Material(shader);

		cam.clearFlags = CameraClearFlags.Nothing;

		cells_order = new CellSortInfo[8];

		node_stack = new NodeStackItem[32];
		linked_idxyz = new IdXYZ[32];
		for (int j = 0; j < node_stack.Length; j++) {
			node_stack[j] = new NodeStackItem();

			// the 1st and the 7 following
			var idxyz0 = new IdXYZ();
			var idxyz = idxyz0;
			for (int i = 1; i < 8; i++) {
				idxyz.next = new IdXYZ();
				idxyz = idxyz.next;
			}

			linked_idxyz[j] = idxyz0;
		}

		stopwatch = new System.Diagnostics.Stopwatch();
	}
	
	class CellSortInfo {
		public int index;
		public float distance;
		public Vector3 position_bounds;
		public Vector3 position;
		public Vector3 position_local;
		public Vector3 projected;
		public Vector3 delta;
		public double depth;
		public double depth_delta;
		public CellSortInfo(int index, float distance, Vector3 position, Vector3 position_local, Vector3 projected) {
			this.index = index;
			this.distance = distance;
			this.position = position;
			this.position_local = position_local;
			this.projected = projected;
			this.delta = Vector3.zero;
			this.depth = 0;
			this.depth_delta = 0;
		}
	}
	static CellSortInfo[] cells_order;

	public bool DrawBounds = false;
	public int LOD = 1; // cell diameter in pixels
	public int OrthoSwitchThreshold = 1; // in pixels

	public bool OffsetByBounds = true;

	static int depth_buffer_bits = 0;
	static float depth_max = 0;
	static float clip_near, clip_far;
	static float persp_zoom_factor;

	static float lod;
	static float ortho_switch_threshold;

	static int BufW, BufH;
	static int BufW1, BufH1;
	static float BufW2, BufH2;

	public int ScrW, ScrH; // to see current resolution
	
	public int RenderDuration = 0;

	public int ProcessedCells = 0;
	public int OrthoCells = 0;
	public int PerspCells = 0;
	public int SkippedCells = 0;
	public int NonleafCells = 0;
	public int LeafCells = 0;
	public int PixelCells = 0;

	static int processed_cells = 0;
	static int ortho_cells = 0;
	static int persp_cells = 0;
	static int skipped_cells = 0;
	static int nonleaf_cells = 0;
	static int leaf_cells = 0;
	static int pixel_cells = 0;

	void Update() {
		if (Input.GetKeyDown(KeyCode.LeftBracket)) LOD = Mathf.Max(LOD - 1, 0);
		if (Input.GetKeyDown(KeyCode.RightBracket)) LOD = Mathf.Min(LOD + 1, 64);
		if (Input.GetKeyDown(KeyCode.Comma)) OrthoSwitchThreshold = Mathf.Max(OrthoSwitchThreshold - 1, 0);
		if (Input.GetKeyDown(KeyCode.Period)) OrthoSwitchThreshold = Mathf.Min(OrthoSwitchThreshold + 1, 64);
		if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) ResolutionFactor = Mathf.Max(ResolutionFactor - 1, 1);
		if (Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus)) ResolutionFactor = Mathf.Min(ResolutionFactor + 1, 8);
	}

	void OnGUI() {
		int line_h = GUI.skin.font.lineHeight;
		int line_y = Screen.height - line_h - 2;
		int numbers_x = 64;
		GUI.Label(new Rect(0, line_y, 200, 20), "Pixel:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), PixelCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Leaf:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), LeafCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Nonleaf:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), NonleafCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Skipped:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), SkippedCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Persp:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), PerspCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Ortho:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), OrthoCells.ToString());
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Total:");
		GUI.Label(new Rect(numbers_x, line_y, 200, 20), ProcessedCells.ToString());

		line_y -= line_h;

		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Ortho error: "+OrthoSwitchThreshold);
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "LOD: "+LOD);
		line_y -= line_h;
		GUI.Label(new Rect(0, line_y, 200, 20), "Subresolution: "+ResolutionFactor);
	}

	void OnPreCull() {
		var main_cam = Camera.main;
		cam.orthographic = main_cam.orthographic;
		cam.orthographicSize = main_cam.orthographicSize;
		cam.fieldOfView = main_cam.fieldOfView;
		cam.depth = main_cam.depth - 1;
		cam.farClipPlane = main_cam.farClipPlane;
		cam.nearClipPlane = main_cam.nearClipPlane;
		cam.rect = main_cam.rect;

		cam.transform.position = main_cam.transform.position;
		cam.transform.rotation = main_cam.transform.rotation;
	}

	void OnPreRender() {
		var main_cam = Camera.main;

		BufW = main_cam.pixelWidth / ResolutionFactor;
		BufH = main_cam.pixelHeight / ResolutionFactor;

		BufW1 = BufW-1;
		BufH1 = BufH-1;

		ScrW = BufW;
		ScrH = BufH;

		// This is necessary for world->screen calculations
		cam.pixelRect = new Rect(0, 0, BufW, BufH);

		if ((!texture) || (texture.width != BufW) || (texture.height != BufH)) {
			if (texture) Destroy(texture);
			texture = new Texture2D(BufW, BufH, format, false);
			texture.filterMode = FilterMode.Point;
			color_buffer = new Color32[BufW*BufH];
			depth_buffer = new float[BufW*BufH];
			//depth_buffer = new int[BufW*BufH];
			material.mainTexture = texture;
		}

		stopwatch.Reset();
		stopwatch.Start();

		RenderVoxels();

		stopwatch.Stop();
		RenderDuration = (int)stopwatch.ElapsedMilliseconds;
		
		texture.SetPixels32(color_buffer, 0);
		texture.Apply(false);

		// Make sure the camera actually covers the whole screen
		cam.pixelRect = main_cam.pixelRect;
	}

	void OnPostRender() {
		material.SetPass(0);
		GL.PushMatrix();
		GL.LoadOrtho(); // near: -1, far: 100
		GL.Begin(GL.QUADS);
		GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
		GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
		GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
		GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
		GL.End();
		GL.PopMatrix();
	}

	void RenderVoxels() {
		depth_max = (cam.farClipPlane - cam.nearClipPlane);

		BufW2 = BufW * 0.5f;
		BufH2 = BufH * 0.5f;
		
		persp_zoom_factor = BufH2 / Mathf.Tan(Mathf.Deg2Rad*cam.fieldOfView*0.5f);

		clip_near = cam.nearClipPlane;
		clip_far = cam.farClipPlane;
		depth_buffer_bits = DepthBufferBits;

		lod = LOD;
		ortho_switch_threshold = OrthoSwitchThreshold;

		processed_cells = 0;
		ortho_cells = 0;
		persp_cells = 0;
		skipped_cells = 0;
		nonleaf_cells = 0;
		leaf_cells = 0;
		pixel_cells = 0;

		ClearRemovedObjects();

		voxel_objs.Sort((voxObjA, voxObjB) => {
			//float zA = tfm.InverseTransformPoint(voxObjA.transform.position).z;
			//float zB = tfm.InverseTransformPoint(voxObjB.transform.position).z;
			float zA = CalcNearestZ(voxObjA);
			float zB = CalcNearestZ(voxObjB);
			return zA.CompareTo(zB);
		});

		System.Array.Clear(color_buffer, 0, color_buffer.Length);
		//System.Array.Clear(depth_buffer, 0, depth_buffer.Length);
		//FillArray(depth_buffer, (1 << depth_buffer_bits)-1);
		FillArray(depth_buffer, Mathf.Infinity);
		foreach (var voxel_obj in voxel_objs) {
			RenderVoxelObj(voxel_obj);
		}

		ProcessedCells = processed_cells;
		OrthoCells = ortho_cells;
		PerspCells = persp_cells;
		SkippedCells = skipped_cells;
		NonleafCells = nonleaf_cells;
		LeafCells = leaf_cells;
		PixelCells = pixel_cells;
	}

	static void FillArray<T>(T[] array, T value) {
		for (int i = 0; i < array.Length; ++i) { array[i] = value; }
	}

	float CalcNearestZ(VoxelObject voxel_obj) {
		float src_local_size = voxel_obj.Scale * 0.5f;
		int octree_size = (1 << voxel_obj.octree.MaxDepth);
		if (voxel_obj.UseOctreeSize) src_local_size *= octree_size;

		Vector3 offset = (voxel_obj.octree.bounds.center * (1f / octree_size)) * 2f;
		if (!OffsetByBounds) offset = Vector3.zero;

		var bounds_scales = voxel_obj.octree.bounds.extents * (1f / octree_size) * 2f * src_local_size;

		float nearest_z = 0;
		bool z_initialized = false;

		var obj_matrix = voxel_obj.transform.localToWorldMatrix;
		var look_matrix = tfm.worldToLocalMatrix;
		for (int z = -1; z <= 1; z += 2) {
			for (int y = -1; y <= 1; y += 2) {
				for (int x = -1; x <= 1; x += 2) {
					//var p = ((new Vector3(x, y, z)) - offset) * src_local_size;
					var p = Vector3.Scale(new Vector3(x, y, z), bounds_scales);
					p = obj_matrix.MultiplyPoint3x4(p);
					var look_p = look_matrix.MultiplyPoint3x4(p);
					if (z_initialized) {
						nearest_z = Mathf.Min(nearest_z, look_p.z);
					} else {
						nearest_z = look_p.z;
						z_initialized = true;
					}
				}
			}
		}

		nearest_z = Mathf.Abs(nearest_z);

		return nearest_z;
	}

	float CalcVertices(VoxelObject voxel_obj, out Bounds screen_bounds, out Vector4 offset_scale) {
		float src_local_size = voxel_obj.Scale * 0.5f;
		int octree_size = (1 << voxel_obj.octree.MaxDepth);
		if (voxel_obj.UseOctreeSize) src_local_size *= octree_size;

		Vector3 offset = (voxel_obj.octree.bounds.center * (1f / octree_size)) * 2f;
		if (!OffsetByBounds) offset = Vector3.zero;

		offset_scale = new Vector4(offset.x, offset.y, offset.z, src_local_size);

		float local_size = 0; // radius of encircling sphere
		var obj_pos = voxel_obj.transform.position;
		var obj_matrix = voxel_obj.transform.localToWorldMatrix;
		var look_matrix = tfm.worldToLocalMatrix;
		var look_dir = tfm.forward;
		var look_pos = tfm.position;
		var look_obj_pos = look_matrix.MultiplyPoint3x4(obj_pos);

		float z_epsilon = Mathf.Min(0.001f, cam.nearClipPlane);

		bool bounds_initialized = false;
		screen_bounds = default(Bounds);

		var bounds_scales = voxel_obj.octree.bounds.extents * (1f / octree_size) * 2f * src_local_size;

		int i = 0;
		for (int z = -1; z <= 1; z += 2) {
			for (int y = -1; y <= 1; y += 2) {
				for (int x = -1; x <= 1; x += 2) {
					var p = ((new Vector3(x, y, z)) - offset) * src_local_size;
					p = obj_matrix.MultiplyPoint3x4(p);

					var look_p = look_matrix.MultiplyPoint3x4(p);
					local_size = Mathf.Max(local_size, (look_obj_pos - look_p).magnitude);

					var proj = cam.WorldToScreenPoint(p);
					proj.z = look_p.z;

					var csi = new CellSortInfo(i, proj.z, p, look_p, proj);
					if (OffsetByBounds) {
						csi.position_bounds = obj_matrix.MultiplyPoint3x4(
							Vector3.Scale(new Vector3(x, y, z), bounds_scales));
					} else {
						csi.position_bounds = p;
					}
					cells_order[i] = csi;

					look_p.z = Mathf.Max(look_p.z, z_epsilon);
					p = tfm.TransformPoint(look_p);
					proj = cam.WorldToScreenPoint(p);
					proj.z = look_p.z;
					if (bounds_initialized) {
						screen_bounds.Encapsulate(proj);
					} else {
						screen_bounds = new Bounds(proj, Vector3.zero);
						bounds_initialized = true;
					}

					i += 1;
				}
			}
		}

		if (DrawBounds) DrawCubeWire();

		System.Array.Sort(cells_order, delegate(CellSortInfo csi1, CellSortInfo csi2) {
			return csi1.distance.CompareTo(csi2.distance);
		});

		return local_size;
	}

	void DrawCubeWire() {
		Color line_color = new Color(1, 1, 1, 0.5f);
		
		Debug.DrawLine(cells_order[0].position_bounds, cells_order[1].position_bounds, line_color);
		Debug.DrawLine(cells_order[2].position_bounds, cells_order[3].position_bounds, line_color);
		Debug.DrawLine(cells_order[4].position_bounds, cells_order[5].position_bounds, line_color);
		Debug.DrawLine(cells_order[6].position_bounds, cells_order[7].position_bounds, line_color);
		
		Debug.DrawLine(cells_order[0].position_bounds, cells_order[2].position_bounds, line_color);
		Debug.DrawLine(cells_order[1].position_bounds, cells_order[3].position_bounds, line_color);
		Debug.DrawLine(cells_order[4].position_bounds, cells_order[6].position_bounds, line_color);
		Debug.DrawLine(cells_order[5].position_bounds, cells_order[7].position_bounds, line_color);
		
		Debug.DrawLine(cells_order[0].position_bounds, cells_order[4].position_bounds, line_color);
		Debug.DrawLine(cells_order[1].position_bounds, cells_order[5].position_bounds, line_color);
		Debug.DrawLine(cells_order[2].position_bounds, cells_order[6].position_bounds, line_color);
		Debug.DrawLine(cells_order[3].position_bounds, cells_order[7].position_bounds, line_color);
	}
	
	void RenderVoxelObj(VoxelObject voxel_obj) {
		if (!voxel_obj) return;
		if (voxel_obj.octree == null) return;
		if (!(voxel_obj.enabled && voxel_obj.gameObject.activeInHierarchy)) return;

		bool ortho = cam.orthographic;

		Vector4 offset_scale;
		Bounds screen_bounds; // not clipped to camera render borders
		float local_size = CalcVertices(voxel_obj, out screen_bounds, out offset_scale);

		float x0, y0, z, rx, ry;
		if (ortho) {
			Vector3 projected_pos = cam.WorldToScreenPoint(voxel_obj.transform.position);
			float projected_size = 0;
			float dpx = 0, dpy = 0; // NOT the same as screen_bounds!
			for (int i = 0; i < 8; i++) {
				var csi = cells_order[i];
				csi.delta = (csi.projected - projected_pos);
				projected_size = Mathf.Max(projected_size, csi.delta.magnitude);
				dpx = Mathf.Max(dpx, Mathf.Abs(csi.delta.x));
				dpy = Mathf.Max(dpy, Mathf.Abs(csi.delta.y));
				cells_order[i] = csi;
			}

			rx = dpx;
			ry = dpy;
			x0 = projected_pos.x;
			y0 = projected_pos.y;
			z = projected_pos.z;
		} else {
			Vector3 local_pos = tfm.InverseTransformPoint(voxel_obj.transform.position);
			for (int i = 0; i < 8; i++) {
				var csi = cells_order[i];
				csi.delta = (csi.position_local - local_pos);
				cells_order[i] = csi;
			}

			rx = local_size;
			ry = local_size;
			x0 = local_pos.x;
			y0 = local_pos.y;
			z = local_pos.z;
		}

		radiuses[0] = new Vector4(rx-0.5f, ry-0.5f, Mathf.Max(rx, ry), local_size);

		var idxyz = linked_idxyz[0];
		for (int i = 0; i < 8; i++) {
			var csi = cells_order[i];
			idxyz.index = csi.index;
			idxyz.dx = csi.delta.x * 0.5f;
			idxyz.dy = csi.delta.y * 0.5f;
			idxyz.dz = csi.delta.z * 0.5f;
			idxyz = idxyz.next;
		}

		RasterizeOctree(voxel_obj.octree.root, x0, y0, z, ortho, voxel_obj.tint);
	}

	struct NodeStackItem {
		public IdXYZ idxyz;
		public VoxelObject.OctreeNode node;
		public float x, y, z;
		public bool ortho;
		public bool inside;
	}
	static NodeStackItem[] node_stack = null;

	class IdXYZ {
		public int index;
		public float dx, dy, dz;
		public IdXYZ next;
	}
	static IdXYZ[] linked_idxyz;

	static Vector4[] radiuses = new Vector4[32];

	static void RasterizeOctree(VoxelObject.OctreeNode node, float x0f, float y0f, float z, bool ortho, Color32 tint) {
		#region various initialization
		int stack_id = 0, stack_size = 1;
		
		var nsi = new NodeStackItem();
		nsi.node = node;
		nsi.x = x0f;
		nsi.y = y0f;
		nsi.z = z;
		nsi.ortho = ortho;
		nsi.inside = false;
		node_stack[0] = nsi;
		
		VoxelObject.OctreeNode subnode = null;
		
		float lod_r = lod * 0.5f;
		float lod_factor = lod_r / persp_zoom_factor;

		float dk_ortho = ortho_switch_threshold * 0.5f;

		float depth_offset = -clip_near, depth_scale = ((1 << depth_buffer_bits) - 1) / (clip_far - clip_near);

		Color32 c = default(Color32);

		bool is_leaf = false;

		float xMinF = 0, xMaxF = 0, yMinF = 0, yMaxF = 0;
		int xMin = 0, xMax = 0, yMin = 0, yMax = 0;

		float BufW1f = BufW1+0.5f, BufH1f = BufH1+0.5f;
		float BufW1f1 = BufW1f-1f, BufH1f1 = BufH1f-1f;

		float proj_cx = 0, proj_cy = 0, proj_r = 0;
		#endregion

	branch_down:;

		bool switch_to_ortho = false;
//		int inside_flag = 0;

		++processed_cells;

		var r = radiuses[stack_id];
		float closest_z = nsi.z - r.w, farthest_z = nsi.z + r.w;
		if ((closest_z > clip_far) || (farthest_z < clip_near)) {
			goto branch_up;
		} else if (nsi.z < clip_near) {
			c = nsi.node.color; // a == 255 is considered a leaf
			if (c.a == 255) { goto branch_up; } else { goto skip_occlusion; }
		}

		if (nsi.ortho) {
			++ortho_cells;

			c = nsi.node.color; // a == 255 is considered a leaf
			is_leaf = ((c.a == 255) || (r.z <= lod_r)); // r.z is max(r.x, r.y)
			
			// if intersects near plane -> no sense in trying to compute occlusion
			if (closest_z < clip_near) {
				if (!is_leaf) {
					goto skip_occlusion;
//				} else {
//					inside_flag = -1;
				}
			}

			xMinF = (nsi.x - r.x);
			xMaxF = (nsi.x + r.x);
			yMinF = (nsi.y - r.y);
			yMaxF = (nsi.y + r.y);
		} else {
			++persp_cells;

			float closest_z_clamped;
			if (closest_z < clip_near) closest_z_clamped = clip_near; else closest_z_clamped = closest_z;
			
			c = nsi.node.color; // a == 255 is considered a leaf
			is_leaf = ((c.a == 255) || (r.w <= lod_factor * closest_z_clamped));
			
			// if intersects near plane -> no sense in trying to compute occlusion
			if (closest_z < clip_near) {
				if (!is_leaf) {
					goto skip_occlusion;
//				} else {
//					inside_flag = -1;
				}
			}

			//float k_farthest = persp_zoom_factor / farthest_z;
			float k_closest = persp_zoom_factor / closest_z_clamped;
			float k_center = persp_zoom_factor / nsi.z;

			// If the min/max projections have less than 1 pixel difference, we can
			// switch to ortho. Ideally, this should check the difference between
			// closest and furthest, but seems like (closest - center) works fine too.
			switch_to_ortho = (!is_leaf) && ((k_closest - k_center) < dk_ortho);

			proj_cx = BufW2 + nsi.x * k_center;
			proj_cy = BufH2 + nsi.y * k_center;
			proj_r = (r.w * k_closest) - 0.5f;

			xMinF = (proj_cx - proj_r);
			xMaxF = (proj_cx + proj_r);
			yMinF = (proj_cy - proj_r);
			yMaxF = (proj_cy + proj_r);
		}

		if (xMinF < 0f) { xMinF = 0f; }
		if (xMaxF > BufW1f) { xMaxF = BufW1f; }
		if (yMinF < 0f) { yMinF = 0f; }
		if (yMaxF > BufH1f) { yMaxF = BufH1f; }

//		if (xMinF < 1f) { inside_flag = -1; }
//		if (xMaxF > BufW1f1) { inside_flag = -1; }
//		if (yMinF < 1f) { inside_flag = -1; }
//		if (yMaxF > BufH1f1) { inside_flag = -1; }

		xMin = (int)xMinF;
		xMax = (int)xMaxF;
		yMin = (int)yMinF;
		yMax = (int)yMaxF;

		#region rasterization / occlusion test
		if ((xMax < xMin) || (yMax < yMin)) {
			++skipped_cells; goto branch_up;
		} else if ((xMax == xMin) && (yMax == yMin)) {
			int i = xMin + yMin * BufW;
			if (is_leaf) {
				float depth = nsi.z;
				//float depth = (nsi.z + depth_offset) * depth_scale;
				//int depth = (int)((nsi.z + depth_offset) * depth_scale);
				if (depth < depth_buffer[i]) {
					c.r = (byte)((c.r * tint.r + 255) >> 8); c.g = (byte)((c.g * tint.g + 255) >> 8); c.b = (byte)((c.b * tint.b + 255) >> 8);
					depth_buffer[i] = depth; color_buffer[i] = c;
				}
				++pixel_cells; goto branch_up;
			} else {
//				nsi.inside = (inside_flag != -1);
				float depth = closest_z;
				//float depth = (closest_z + depth_offset) * depth_scale;
				//int depth = (int)((closest_z + depth_offset) * depth_scale);
				if (depth < depth_buffer[i]) { goto skip_occlusion; }
				++skipped_cells; goto branch_up;
			}
		} else {
			int i_row = xMin + yMin * BufW;
			int row_width = (xMax - xMin);
			if (is_leaf) {
				float depth = nsi.z;
				//float depth = (nsi.z + depth_offset) * depth_scale;
				//int depth = (int)((nsi.z + depth_offset) * depth_scale);
				c.r = (byte)((c.r * tint.r + 255) >> 8); c.g = (byte)((c.g * tint.g + 255) >> 8); c.b = (byte)((c.b * tint.b + 255) >> 8);
				for (;;) {
					int i_row1 = i_row + row_width;
					int i = i_row;
					while (i <= i_row1) {
						if (depth < depth_buffer[i]) { depth_buffer[i] = depth; color_buffer[i] = c; } ++i;
					}
					if (++yMin > yMax) { ++leaf_cells; goto branch_up; }
					i_row += BufW;
				}
			} else {
//				nsi.inside = (inside_flag != -1);
				float depth = closest_z;
				//float depth = (closest_z + depth_offset) * depth_scale;
				//int depth = (int)((closest_z + depth_offset) * depth_scale);
				for (;;) {
					int i_row1 = i_row + row_width;
					int i = i_row;
					while (i <= i_row1) {
						if (depth < depth_buffer[i]) { goto skip_occlusion; } ++i;
					}
					if (++yMin > yMax) { ++skipped_cells; goto branch_up; }
					i_row += BufW;
				}
			}
		}
		#endregion

	skip_occlusion:;

		#region persp -> ortho switch
		// ortho switch is performed here to not do extra work if the node is occluded
		if (switch_to_ortho) {
			bool bbox_initialized = false;
			float b0x = 0, b0y = 0, b1y = 0, b1x = 0, b0k = 0, b1k = 0;

			var idxyz0 = linked_idxyz[stack_id];
			var idxyz1 = linked_idxyz[linked_idxyz.Length-1];

			while (idxyz0 != null) {
				float wrk_x = nsi.x + (idxyz0.dx + idxyz0.dx);
				float wrk_y = nsi.y + (idxyz0.dy + idxyz0.dy);
				float wrk_z = nsi.z + (idxyz0.dz + idxyz0.dz);

				float wrk_k = persp_zoom_factor / wrk_z;

				float wrk_px = (BufW2 + wrk_x * wrk_k);
				float wrk_py = (BufH2 + wrk_y * wrk_k);

				if (bbox_initialized) {
					if (wrk_px < b0x) { b0x = wrk_px; } else if (wrk_px > b1x) { b1x = wrk_px; }
					if (wrk_py < b0y) { b0y = wrk_py; } else if (wrk_py > b1y) { b1y = wrk_py; }
					if (wrk_k < b0k) { b0k = wrk_k; } else if (wrk_k > b1k) { b1k = wrk_k; }
				} else {
					b0x = b1x = wrk_px;
					b0y = b1y = wrk_py;
					b0k = b1k = wrk_k;
					bbox_initialized = true;
				}

				idxyz1.dx = (wrk_px - proj_cx) * 0.5f;
				idxyz1.dy = (wrk_py - proj_cy) * 0.5f;
				idxyz1.dz = (wrk_z - nsi.z) * 0.5f;

				idxyz0 = idxyz0.next;
				idxyz1 = idxyz1.next;
			}

			// Make more precise check, otherwise there can be glitches.
			switch_to_ortho = ((b1k - b0k) < ortho_switch_threshold);
			if (switch_to_ortho) {
				switch_to_ortho = false;

				idxyz0 = linked_idxyz[stack_id];
				idxyz1 = linked_idxyz[linked_idxyz.Length-1];

				while (idxyz0 != null) {
					idxyz0.dx = idxyz1.dx;
					idxyz0.dy = idxyz1.dy;
					idxyz0.dz = idxyz1.dz;
					
					idxyz0 = idxyz0.next;
					idxyz1 = idxyz1.next;
				}

				nsi.ortho = true;
				nsi.x = proj_cx;
				nsi.y = proj_cy;
				
				node_stack[stack_id] = nsi;
				
				stack_size = stack_id+1; // invalidate cached values of perspective projection
				
				var rOrt = radiuses[stack_id];
				rOrt.x = (b1x - b0x) * 0.5f;
				rOrt.y = (b1y - b0y) * 0.5f;
				rOrt.z = rOrt.x; if (rOrt.y > rOrt.z) rOrt.z = rOrt.y;
				rOrt.x -= 0.5f;
				rOrt.y -= 0.5f;
				// w (abs radius) stays the same
				radiuses[stack_id] = rOrt;
			}
		}
		#endregion

		++nonleaf_cells;
		
		nsi.idxyz = linked_idxyz[stack_id];

	resume_subnodes:;
		
		switch (nsi.idxyz.index) {
		case 0: subnode = nsi.node.n000; break;
		case 1: subnode = nsi.node.n001; break;
		case 2: subnode = nsi.node.n010; break;
		case 3: subnode = nsi.node.n011; break;
		case 4: subnode = nsi.node.n100; break;
		case 5: subnode = nsi.node.n101; break;
		case 6: subnode = nsi.node.n110; break;
		case 7: subnode = nsi.node.n111; break;
		}
		
		if (subnode == null) {
			nsi.idxyz = nsi.idxyz.next;
			if (nsi.idxyz == null) goto branch_up;
			goto resume_subnodes;
		}
		
		IdXYZ idxyz = nsi.idxyz;
		nsi.idxyz = nsi.idxyz.next;

		node_stack[stack_id] = nsi;
		++stack_id;

		if (stack_id == stack_size) {
			#region initialize next level's look-up tables from the current level
			r = radiuses[stack_id-1];
			r.x = r.x * 0.5f - 0.25f;
			r.y = r.y * 0.5f - 0.25f;
			r.z *= 0.5f;
			r.w *= 0.5f;
			radiuses[stack_id] = r;

			var idxyz0 = linked_idxyz[stack_id-1];
			var idxyz1 = linked_idxyz[stack_id];
			while (idxyz0 != null) {
				idxyz1.index = idxyz0.index;
				idxyz1.dx = idxyz0.dx * 0.5f;
				idxyz1.dy = idxyz0.dy * 0.5f;
				idxyz1.dz = idxyz0.dz * 0.5f;
				idxyz0 = idxyz0.next;
				idxyz1 = idxyz1.next;
			}
			#endregion

			++stack_size;
		}

		nsi.node = subnode;
		nsi.x += idxyz.dx;
		nsi.y += idxyz.dy;
		nsi.z += idxyz.dz;

//		if (nsi.ortho && nsi.inside) {
//			RasterizeOctreeInsideOrtho(nsi, stack_id, stack_size, tint);
//			goto branch_up;
//		}

		goto branch_down;
		
	branch_up:;

		if (stack_id == 0) return; // EXIT POINT

		bool old_ortho = nsi.ortho;
		nsi = node_stack[--stack_id];

		if (nsi.ortho != old_ortho) stack_size = stack_id+1; // invalidate cached values of ortho projection

		// (nsi.idxyz == null) means that all 8 subnodes were processed and we can return to the upper node
		if (nsi.idxyz == null) { goto branch_up; } else { goto resume_subnodes; }
	}

	static void RasterizeOctreeInsideOrtho(NodeStackItem nsi, int stack_id, int stack_size, Color32 tint) {
		#region various initialization
		int stack_id_0 = stack_id;

		VoxelObject.OctreeNode subnode = null;
		
		float lod_r = lod * 0.5f;

		float depth_offset = -clip_near, depth_scale = ((1 << depth_buffer_bits) - 1) / (clip_far - clip_near);
		
		Color32 c = default(Color32);
		
		bool is_leaf = false;
		
		float xMinF = 0, xMaxF = 0, yMinF = 0, yMaxF = 0;
		int xMin = 0, xMax = 0, yMin = 0, yMax = 0;
		
		float BufW1f = BufW1+0.5f, BufH1f = BufH1+0.5f;
		#endregion
		
	branch_down:;
		
		++processed_cells;
		
		var r = radiuses[stack_id];
		float closest_z = nsi.z - r.w;

		{
			++ortho_cells;
			
			c = nsi.node.color; // a == 255 is considered a leaf
			is_leaf = ((c.a == 255) || (r.z <= lod_r)); // r.z is max(r.x, r.y)
			
			xMinF = (nsi.x - r.x);
			xMaxF = (nsi.x + r.x);
			yMinF = (nsi.y - r.y);
			yMaxF = (nsi.y + r.y);
		}

		xMin = (int)xMinF;
		xMax = (int)xMaxF;
		yMin = (int)yMinF;
		yMax = (int)yMaxF;
		
		#region rasterization / occlusion test
		if ((xMax < xMin) || (yMax < yMin)) {
			++skipped_cells; goto branch_up;
		} else if ((xMax == xMin) && (yMax == yMin)) {
			int i = xMin + yMin * BufW;
			if (is_leaf) {
				float depth = nsi.z;
				//float depth = (nsi.z + depth_offset) * depth_scale;
				//int depth = (int)((nsi.z + depth_offset) * depth_scale);
				if (depth < depth_buffer[i]) {
					c.r = (byte)((c.r * tint.r + 255) >> 8); c.g = (byte)((c.g * tint.g + 255) >> 8); c.b = (byte)((c.b * tint.b + 255) >> 8);
					depth_buffer[i] = depth; color_buffer[i] = c;
				}
				++pixel_cells; goto branch_up;
			} else {
				float depth = closest_z;
				//float depth = (closest_z + depth_offset) * depth_scale;
				//int depth = (int)((closest_z + depth_offset) * depth_scale);
				if (depth < depth_buffer[i]) { goto skip_occlusion; }
				++skipped_cells; goto branch_up;
			}
		} else {
			int i_row = xMin + yMin * BufW;
			int row_width = (xMax - xMin);
			if (is_leaf) {
				float depth = nsi.z;
				//float depth = (nsi.z + depth_offset) * depth_scale;
				//int depth = (int)((nsi.z + depth_offset) * depth_scale);
				c.r = (byte)((c.r * tint.r + 255) >> 8); c.g = (byte)((c.g * tint.g + 255) >> 8); c.b = (byte)((c.b * tint.b + 255) >> 8);
				for (;;) {
					int i_row1 = i_row + row_width;
					int i = i_row;
					while (i <= i_row1) {
						if (depth < depth_buffer[i]) { depth_buffer[i] = depth; color_buffer[i] = c; } ++i;
					}
					if (++yMin > yMax) { ++leaf_cells; goto branch_up; }
					i_row += BufW;
				}
			} else {
				float depth = closest_z;
				//float depth = (closest_z + depth_offset) * depth_scale;
				//int depth = (int)((closest_z + depth_offset) * depth_scale);
				for (;;) {
					int i_row1 = i_row + row_width;
					int i = i_row;
					while (i <= i_row1) {
						if (depth < depth_buffer[i]) { goto skip_occlusion; } ++i;
					}
					if (++yMin > yMax) { ++skipped_cells; goto branch_up; }
					i_row += BufW;
				}
			}
		}
		#endregion
		
	skip_occlusion:;

		++nonleaf_cells;
		
		nsi.idxyz = linked_idxyz[stack_id];
		
	resume_subnodes:;
		
		switch (nsi.idxyz.index) {
		case 0: subnode = nsi.node.n000; break;
		case 1: subnode = nsi.node.n001; break;
		case 2: subnode = nsi.node.n010; break;
		case 3: subnode = nsi.node.n011; break;
		case 4: subnode = nsi.node.n100; break;
		case 5: subnode = nsi.node.n101; break;
		case 6: subnode = nsi.node.n110; break;
		case 7: subnode = nsi.node.n111; break;
		}
		
		if (subnode == null) {
			nsi.idxyz = nsi.idxyz.next;
			if (nsi.idxyz == null) goto branch_up;
			goto resume_subnodes;
		}
		
		IdXYZ idxyz = nsi.idxyz;
		nsi.idxyz = nsi.idxyz.next;
		
		node_stack[stack_id] = nsi;
		++stack_id;
		
		if (stack_id == stack_size) {
			#region initialize next level's look-up tables from the current level
			r = radiuses[stack_id-1];
			r.x = r.x * 0.5f - 0.25f;
			r.y = r.y * 0.5f - 0.25f;
			r.z *= 0.5f;
			r.w *= 0.5f;
			radiuses[stack_id] = r;
			
			var idxyz0 = linked_idxyz[stack_id-1];
			var idxyz1 = linked_idxyz[stack_id];
			while (idxyz0 != null) {
				idxyz1.index = idxyz0.index;
				idxyz1.dx = idxyz0.dx * 0.5f;
				idxyz1.dy = idxyz0.dy * 0.5f;
				idxyz1.dz = idxyz0.dz * 0.5f;
				idxyz0 = idxyz0.next;
				idxyz1 = idxyz1.next;
			}
			#endregion
			
			++stack_size;
		}
		
		nsi.node = subnode;
		nsi.x += idxyz.dx;
		nsi.y += idxyz.dy;
		nsi.z += idxyz.dz;
		
		goto branch_down;
		
	branch_up:;
		
		if (stack_id <= stack_id_0) return; // EXIT POINT
		
		bool old_ortho = nsi.ortho;
		nsi = node_stack[--stack_id];
		
		if (nsi.ortho != old_ortho) stack_size = stack_id+1; // invalidate cached values of ortho projection
		
		// (nsi.idxyz == null) means that all 8 subnodes were processed and we can return to the upper node
		if (nsi.idxyz == null) { goto branch_up; } else { goto resume_subnodes; }
	}
}
