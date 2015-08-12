using UnityEngine;
using System.Collections.Generic;

public class VoxelObject : MonoBehaviour {
	public TextAsset octree_data;

	public Color32 tint = Color.white;

	public string color_name = "";
	public string normal_name = "";
	public string ao_name = "";

	public bool UseOctreeSize = false;
	public float Scale = 1;

	public int MaxVizLevel = 32;

	public bool OverrideColor = false;

	public bool PrintChannelNames = false;
	public int NodesCount = 0;
	public int MaxDepth = 0;

	bool min_max_initialized = false;
	public int xMin { get; set; }
	public int xMax { get; set; }
	public int yMin { get; set; }
	public int yMax { get; set; }
	public int zMin { get; set; }
	public int zMax { get; set; }
	public Bounds bounds;

	void Start() {
		if (VoxelRenderer.instance != null) {
			VoxelRenderer.instance.AddVoxelObject(this);
		}
	}

	void OnDestroy() {
		if (VoxelRenderer.instance != null) {
			VoxelRenderer.instance.RemoveVoxelObject(this);
		}
	}

	public class Octree {
		public OctreeNode root = null;
		public Bounds bounds;
		public int NodesCount = 0;
		public int MaxDepth = 0;
	}
	public Octree octree = null;

	public class OctreeNode {
		public OctreeNode n000;
		public OctreeNode n001;
		public OctreeNode n010;
		public OctreeNode n011;
		public OctreeNode n100;
		public OctreeNode n101;
		public OctreeNode n110;
		public OctreeNode n111;

		public Color32 color;

		public OctreeNode this[int index] {
			get {
				switch (index) {
				case 0: return n000;
				case 1: return n001;
				case 2: return n010;
				case 3: return n011;
				case 4: return n100;
				case 5: return n101;
				case 6: return n110;
				case 7: return n111;
				default: return null;
				}
			}
			set {
				switch (index) {
				case 0: n000 = value; break;
				case 1: n001 = value; break;
				case 2: n010 = value; break;
				case 3: n011 = value; break;
				case 4: n100 = value; break;
				case 5: n101 = value; break;
				case 6: n110 = value; break;
				case 7: n111 = value; break;
				}
			}
		}
	}
	
	// =============================== //

	class VoxelAttrib {
		public string name;
		public int i, n;
	}
	List<VoxelAttrib> attribs;
	
	int voxel_len;
	int octree_size;
	
	int color_i0, color_i1;
	byte[] attr_color;
	
	int normal_i0, normal_i1;
	byte[] attr_normal;
	
	int ao_i0, ao_i1;
	byte[] attr_ao;
	
	public Octree ReadOctree() {
		if (octree == null) {
			if (string.IsNullOrEmpty(color_name)) {
				var octree_text = (octree_data ? octree_data.text : null);
				if (string.IsNullOrEmpty(octree_text)) return null;
				ReadOctree(octree_text);
			} else {
				var octree_bytes = (octree_data ? octree_data.bytes : null);
				if (octree_bytes == null) return null;
				ReadOctree(octree_bytes);
			}
		}
		return octree;
	}

	void ReadOctree(string octree_text) {
		octree = new Octree();
		octree.root = new OctreeNode();

		MaxDepth = 8; // here, we know that octree is always 256-sized
		octree_size = 1 << MaxDepth;

		var line_sep = new char[]{'\r', '\n'};
		var lines = octree_text.Split(line_sep, System.StringSplitOptions.RemoveEmptyEntries);
		foreach (var line in lines) {
			var components = line.Split('\t');
			
			int x = int.Parse(components[0]);
			int y = int.Parse(components[1]);
			int z = int.Parse(components[2]);
			byte R = (byte)int.Parse(components[3]);
			byte G = (byte)int.Parse(components[4]);
			byte B = (byte)int.Parse(components[5]);
			
			AddPoint(x, y, z, R, G, B);
		}

		CalcLODColors(octree.root);
		
		bounds = new Bounds(new Vector3((xMax+xMin)*0.5f, (yMax+yMin)*0.5f, (zMax+zMin)*0.5f),
		                    new Vector3(xMax-xMin, yMax-yMin, zMax-zMin));
		
		octree.bounds = bounds;
		octree.MaxDepth = MaxDepth;
		octree.NodesCount = NodesCount;
	}

	void AddPoint(int x, int y, int z, byte R, byte G, byte B) {
		int hsz = octree_size >> 1;

		NodesCount += 1;

		Color32 color = new Color32(R, G, B, 255);

		var node = octree.root;
		for (int level = MaxDepth-1; level >= 0; level--) {
			int ix = (x >> level) & 1;
			int iy = (y >> level) & 1;
			int iz = (z >> level) & 1;
			int mask = ix | (iy << 1) | (iz << 2);

			OctreeNode subnode = null;
			switch (mask) {
			case 0: subnode = node.n000; break;
			case 1: subnode = node.n001; break;
			case 2: subnode = node.n010; break;
			case 3: subnode = node.n011; break;
			case 4: subnode = node.n100; break;
			case 5: subnode = node.n101; break;
			case 6: subnode = node.n110; break;
			case 7: subnode = node.n111; break;
			}

			if (subnode == null) {
				subnode = new OctreeNode();
				switch (mask) {
				case 0: node.n000 = subnode; break;
				case 1: node.n001 = subnode; break;
				case 2: node.n010 = subnode; break;
				case 3: node.n011 = subnode; break;
				case 4: node.n100 = subnode; break;
				case 5: node.n101 = subnode; break;
				case 6: node.n110 = subnode; break;
				case 7: node.n111 = subnode; break;
				}
			}

			node = subnode;
		}

		node.color = color;
		
		x -= hsz;
		y -= hsz;
		z -= hsz;
		
		if (min_max_initialized) {
			xMin = Mathf.Min(xMin, x);
			xMax = Mathf.Max(xMax, x);
			yMin = Mathf.Min(yMin, y);
			yMax = Mathf.Max(yMax, y);
			zMin = Mathf.Min(zMin, z);
			zMax = Mathf.Max(zMax, z);
		} else {
			xMin = xMax = x;
			yMin = yMax = y;
			zMin = zMax = z;
			min_max_initialized = true;
		}
	}
	void CalcLODColors(OctreeNode node) {
		Vector4 color_accum = Vector4.zero;
		int color_count = 0;
		
		for (int i = 0; i < 8; i++) {
			OctreeNode subnode = null;
			switch (i) {
			case 0: subnode = node.n000; break;
			case 1: subnode = node.n001; break;
			case 2: subnode = node.n010; break;
			case 3: subnode = node.n011; break;
			case 4: subnode = node.n100; break;
			case 5: subnode = node.n101; break;
			case 6: subnode = node.n110; break;
			case 7: subnode = node.n111; break;
			}
			if (subnode == null) continue;

			CalcLODColors(subnode);

			color_accum += ColorToVector(subnode.color);
			color_count += 1;
		}
		
		if (color_count > 0) {
			// in current format, arbitrary-level leaf nodes are not supported
			Color32 color = VectorToColor(color_accum * (1f/color_count));
			color.a = 127; // indicate that this is not a leaf node
			node.color = color;
		}
	}
	
	void ReadOctree(byte[] octree_bytes) {
		var mem_stream = new System.IO.MemoryStream(octree_bytes);
		var bin_stream = new System.IO.BinaryReader(mem_stream);
		ReadOctree(bin_stream);
		bin_stream.Close();
		mem_stream.Close();
	}
	void ReadOctree(System.IO.BinaryReader bin_stream) {
		color_i0 = -1;
		color_i1 = -1;

		normal_i0 = -1;
		normal_i1 = -1;

		ao_i0 = -1;
		ao_i1 = -1;

		attr_color = new byte[3];
		attr_normal = new byte[3]{FloatToByte(0f, true), FloatToByte(1f, true), FloatToByte(0f, true)};
		attr_ao = new byte[3];
		
		ReadAttribs(bin_stream);
		
		int depth = bin_stream.ReadByte();
		octree_size = 1 << depth;

		octree = new Octree();
		octree.root = ReadNode(bin_stream, 0, 0, 0, depth);

		MaxDepth = depth;

		bounds = new Bounds(new Vector3((xMax+xMin)*0.5f, (yMax+yMin)*0.5f, (zMax+zMin)*0.5f),
		                    new Vector3(xMax-xMin, yMax-yMin, zMax-zMin));

		octree.bounds = bounds;
		octree.MaxDepth = MaxDepth;
		octree.NodesCount = NodesCount;
	}

	string ReadString(System.IO.BinaryReader bin_stream) {
		int length = bin_stream.ReadByte();
		if (length == 0) return "";
		var chars = new char[length];
		for (int i = 0; i < length; i++) {
			chars[i] = (char)bin_stream.ReadByte();
		}
		return new string(chars);
	}

	void ReadAttribs(System.IO.BinaryReader bin_stream) {
		attribs = new List<VoxelAttrib>();

		voxel_len = 0;

		int count = bin_stream.ReadByte();
		int i = 0;
		for (int id = 0; id < count; id++) {
			var attrib = new VoxelAttrib();
			attrib.name = ReadString(bin_stream).ToLower();
			attrib.i = i;
			attrib.n = bin_stream.ReadByte();
			i += attrib.n;
			voxel_len += attrib.n;

			attribs.Add(attrib);

			if (PrintChannelNames) Debug.Log(attrib.name);

			if (attrib.name == color_name.ToLower()) {
				color_i0 = attrib.i;
				color_i1 = color_i0 + attrib.n;
			} else if (attrib.name == normal_name.ToLower()) {
				normal_i0 = attrib.i;
				normal_i1 = normal_i0 + attrib.n;
			} else if (attrib.name == ao_name.ToLower()) {
				ao_i0 = attrib.i;
				ao_i1 = ao_i0 + attrib.n;
			}
		}
	}

	OctreeNode ReadNode(System.IO.BinaryReader bin_stream, int x, int y, int z, int depth) {
		var node = new OctreeNode();

		NodesCount += 1;

		byte mask = bin_stream.ReadByte();

		if (mask == 0) {
			for (int i = 0; i < voxel_len; i++) {
				byte b = bin_stream.ReadByte();
				if ((i >= color_i0) && (i < color_i1)) {
					attr_color[i-color_i0] = b;
				} else if ((i >= normal_i0) && (i < normal_i1)) {
					attr_normal[i-normal_i0] = b;
				} else if ((i >= ao_i0) && (i < ao_i1)) {
					attr_ao[i-ao_i0] = b;
				}
			}

			int hsz = octree_size >> 1;

			Color32 color = new Color32(attr_color[0], attr_color[1], attr_color[2], 255);
			if (ao_i0 != -1) {
				color.r = (byte)((color.r * attr_ao[0])/255);
				color.g = (byte)((color.g * attr_ao[1])/255);
				color.b = (byte)((color.b * attr_ao[2])/255);
			}
			float nx = ByteToFloat(attr_normal[0], true);
			float ny = ByteToFloat(attr_normal[1], true);
			float nz = ByteToFloat(attr_normal[2], true);

			node.color = color;

			// Convert from Blender to Unity: (x,y,z) -> (x,z,y)
			int wrk_x = x, wrk_y = y, wrk_z = z;
			x = wrk_x; y = wrk_z; z = wrk_y;

			x -= hsz;
			y -= hsz;
			z -= hsz;

			if (min_max_initialized) {
				xMin = Mathf.Min(xMin, x);
				xMax = Mathf.Max(xMax, x);
				yMin = Mathf.Min(yMin, y);
				yMax = Mathf.Max(yMax, y);
				zMin = Mathf.Min(zMin, z);
				zMax = Mathf.Max(zMax, z);
			} else {
				xMin = xMax = x;
				yMin = yMax = y;
				zMin = zMax = z;
				min_max_initialized = true;
			}
		} else {
			Vector4 color_accum = Vector4.zero;
			int color_count = 0;

			int depth1 = depth-1;
			int halfsize = 1 << depth1;
			for (int i = 0; i < 8; i++) {
				if (((mask >> i) & 1) == 0) continue;

				int dx = ((i >> 0) & 1) * halfsize;
				int dy = ((i >> 1) & 1) * halfsize;
				int dz = ((i >> 2) & 1) * halfsize;

				var subnode = ReadNode(bin_stream, x+dx, y+dy, z+dz, depth1);

				// Convert from Blender to Unity: (x,y,z) -> (x,z,y)
				switch (i) {
//				case 0: node.n000 = subnode; break;
//				case 1: node.n001 = subnode; break;
//				case 2: node.n010 = subnode; break;
//				case 3: node.n011 = subnode; break;
//				case 4: node.n100 = subnode; break;
//				case 5: node.n101 = subnode; break;
//				case 6: node.n110 = subnode; break;
//				case 7: node.n111 = subnode; break;
				case 0: node.n000 = subnode; break;
				case 1: node.n001 = subnode; break;
				case 2: node.n100 = subnode; break;
				case 3: node.n101 = subnode; break;
				case 4: node.n010 = subnode; break;
				case 5: node.n011 = subnode; break;
				case 6: node.n110 = subnode; break;
				case 7: node.n111 = subnode; break;
				default: subnode = null; break;
				}

				color_accum += ColorToVector(subnode.color);
				color_count += 1;
			}

			if (color_count > 0) {
				node.color = VectorToColor(color_accum * (1f/color_count));
			}

			// in current format, arbitrary-level leaf nodes are not supported
			Color32 color = node.color;
			color.a = 127; // indicate that this is not a leaf node
			node.color = color;
		}

		return node;
	}

	static Vector4 ColorToVector(Color c) {
		return new Vector4(c.r, c.g, c.b, c.a);
	}
	static Color VectorToColor(Vector4 v) {
		return new Color(v.x, v.y, v.z, v.w);
	}

	static float ByteToFloat(byte b, bool is_signed) {
		if (!is_signed) return b / 255f;
		return (b < 128) ? ((b-128f) / 128f) : ((b-128f) / 127f);
	}
	static byte FloatToByte(float f, bool is_signed) {
		if (!is_signed) return (byte)Mathf.Round(255f*f);
		return (byte)Mathf.Round(((f < 0f) ? (f * 128f) : (f * 127f)) + 128f);
	}
}
