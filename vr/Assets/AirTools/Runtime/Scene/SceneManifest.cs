using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace AirTools.Scene
{
    /// scene.json of a scene package (backend docs/api.md, implementation plan §1/§1a). The only file the app polls;
    /// it names every other file. Unknown fields are ignored; optional ones may be null or missing.
    public class SceneManifest
    {
        public string name;
        public string units;
        public double[] up;
        public double[] north;
        public string scale_method;
        public double? scale_residual_m;
        public string scale_notes;
        public List<SceneScaleCandidate> scale_candidates = new List<SceneScaleCandidate>();
        public double? gravity_residual_deg;
        public double[] origin_gps;
        public SceneSpawn recommended_spawn;
        public SceneFileRef mesh;
        public SceneFileRef collision;
        public string cameras;
        public SceneStructureRef structure;
        /// Removable components (backend docs/api.md "parts.r&lt;rev&gt;.json"): optional; without it the mesh loads as one piece.
        public ScenePartsRef parts;
        public string quality;
        public int revision = 1;
        public SceneFrameInfo frame;
        public string pipeline_version;

        static readonly JsonSerializerSettings s_Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
        };

        public static SceneManifest Parse(string json)
        {
            var m = JsonConvert.DeserializeObject<SceneManifest>(json, s_Settings) ?? throw new FormatException("empty scene.json");
            if (m.mesh == null || string.IsNullOrEmpty(m.mesh.file))
                throw new FormatException("scene.json has no mesh.file (an old flat package? it needs the revision-named layout)");
            m.scale_candidates ??= new List<SceneScaleCandidate>();
            return m;
        }

        public static T ParseAny<T>(string json) => JsonConvert.DeserializeObject<T>(json, s_Settings);

        public string MeshFile => mesh?.file;
        public string CollisionFile => collision?.file;
        public string StructureFile => structure?.file;
        public bool HasStructure => !string.IsNullOrEmpty(StructureFile);
        public string PartsFile => parts?.file;
        /// scene.json names a parts file: load the split mesh (mesh.parts) instead of mesh.r&lt;rev&gt;.
        public bool HasParts => !string.IsNullOrEmpty(PartsFile);
        public string FrameId => frame?.id ?? "";

        /// Package up (glTF) as a Unity direction; defaults to +Y.
        public Vector3 Up => up != null && up.Length >= 3 ? GltfFrame.ToUnity(up).normalized : Vector3.up;

        /// Yaw (degrees about up) that puts the package's north on Unity +Z; 0 when north is unknown (indoors).
        public float NorthDeg
        {
            get
            {
                if (north == null || north.Length < 3) return 0f;
                var n = Vector3.ProjectOnPlane(GltfFrame.ToUnity(north), Up);
                if (n.sqrMagnitude < 1e-8f) return 0f;
                return -Vector3.SignedAngle(Vector3.forward, n.normalized, Vector3.up);
            }
        }

        /// Spawn (eye position and look target) in Unity package coordinates, when the package gives one.
        public bool TryGetSpawn(out Vector3 eye, out Vector3 look)
        {
            eye = look = default;
            if (recommended_spawn?.pos == null || recommended_spawn.pos.Length < 3) return false;
            eye = GltfFrame.ToUnity(recommended_spawn.pos);
            look = recommended_spawn.look != null && recommended_spawn.look.Length >= 3
                ? GltfFrame.ToUnity(recommended_spawn.look) : eye + Vector3.forward;
            return true;
        }

        public string Describe() =>
            $"{name ?? "?"} r{revision} {quality ?? "?"} · scale {scale_method ?? "?"}" +
            (scale_residual_m.HasValue ? $" ±{scale_residual_m.Value * 100:0.#} cm" : "") +
            (HasStructure ? $" · structure {structure.planes}p/{structure.edges}e/{structure.corners}c/{structure.objects}o" : " · no structure") +
            (HasParts ? $" · parts {parts.components}" : "");
    }

    /// scene.json's `parts` entry: {"file": "parts.r1.json", "schema": "airtools.parts/1", "components": 4,
    /// "mesh": "mesh.parts.r1.glb", "cavities": "cavity.r1.glb", "collision": "collision.parts.r1.glb"}. The file names
    /// are also in parts.r&lt;rev&gt;.json itself (ScenePartsDoc); the entry wins when both are given.
    public class ScenePartsRef
    {
        public string file;
        public string schema;
        public int components;
        public string mesh;
        public string cavities;
        public string collision;
    }

    public class SceneFileRef
    {
        public string file;
        public int triangles;
        public int texture_px;
    }

    public class SceneStructureRef
    {
        public string file;
        public string schema;
        public int edges, corners, planes, objects;
    }

    public class SceneSpawn
    {
        public double[] pos;
        public double[] look;
    }

    public class SceneScaleCandidate
    {
        public string method;
        public double? scale;
        public double? residual_m;
    }

    public class SceneFrameInfo
    {
        public string id;
        public bool? aligned_to_preview;
        public double? alignment_residual_m;
    }

    /// One row of GET /scenes.
    public class SceneListing
    {
        public string site;
        public int revision = 1;
        public string quality;
        public string updated_at;
        public override string ToString() => $"{site} r{revision} {quality}";
    }
}
