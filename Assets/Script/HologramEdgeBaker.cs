using System.Collections.Generic;
using UnityEngine;

// Writes "feature edge" data for the Hologram Blueprint shader into UV2 (TEXCOORD2).
// The mesh is unwelded (3 unique vertices per triangle) and each corner gets a barycentric coordinate.
// Edges shared only with coplanar neighbours (e.g. the diagonal splitting a quad) get +1 on their
// component so the shader hides them; real corners, outlines and silhouettes stay visible.
public static class HologramEdgeBaker
{
    public const float DefaultFeatureAngle = 25f;

    public static void BakeInPlace(Mesh mesh, float featureAngle = DefaultFeatureAngle)
    {
        float cosThreshold = Mathf.Cos(featureAngle * Mathf.Deg2Rad);

        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var uvs = mesh.uv;
        bool hasNormals = normals.Length == vertices.Length;
        bool hasUvs = uvs.Length == vertices.Length;

        // Weld by position so edges between split (flat-shaded) vertices still match.
        var positionIds = new Dictionary<Vector3Int, int>();
        var weld = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var key = Vector3Int.RoundToInt(vertices[i] * 10000f);
            if (!positionIds.TryGetValue(key, out int id))
            {
                id = positionIds.Count;
                positionIds.Add(key, id);
            }
            weld[i] = id;
        }

        int subMeshCount = mesh.subMeshCount;
        var submeshTriangles = new int[subMeshCount][];
        var faceNormals = new List<Vector3>();
        var edgeFaces = new Dictionary<long, List<int>>();

        for (int s = 0; s < subMeshCount; s++)
        {
            var tris = mesh.GetTriangles(s);
            submeshTriangles[s] = tris;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int face = faceNormals.Count;
                var a = vertices[tris[t]];
                faceNormals.Add(Vector3.Cross(vertices[tris[t + 1]] - a, vertices[tris[t + 2]] - a).normalized);
                for (int e = 0; e < 3; e++)
                {
                    long key = EdgeKey(weld[tris[t + (e + 1) % 3]], weld[tris[t + (e + 2) % 3]]);
                    if (!edgeFaces.TryGetValue(key, out var list))
                        edgeFaces[key] = list = new List<int>(2);
                    list.Add(face);
                }
            }
        }

        int vertexCount = faceNormals.Count * 3;
        var outVertices = new Vector3[vertexCount];
        var outNormals = hasNormals ? new Vector3[vertexCount] : null;
        var outUvs = hasUvs ? new Vector2[vertexCount] : null;
        var outBary = new Vector4[vertexCount];
        var outTriangles = new int[subMeshCount][];

        int faceIndex = 0, write = 0;
        for (int s = 0; s < subMeshCount; s++)
        {
            var tris = submeshTriangles[s];
            outTriangles[s] = new int[tris.Length];
            for (int t = 0; t < tris.Length; t += 3, faceIndex++)
            {
                // Component e of the barycentric is 0 along the edge opposite corner e.
                var hide = Vector3.zero;
                for (int e = 0; e < 3; e++)
                {
                    long key = EdgeKey(weld[tris[t + (e + 1) % 3]], weld[tris[t + (e + 2) % 3]]);
                    if (!IsFeatureEdge(edgeFaces[key], faceIndex, faceNormals, cosThreshold))
                        hide[e] = 1f;
                }

                for (int c = 0; c < 3; c++, write++)
                {
                    int src = tris[t + c];
                    outVertices[write] = vertices[src];
                    if (hasNormals) outNormals[write] = normals[src];
                    if (hasUvs) outUvs[write] = uvs[src];
                    var bary = Vector3.zero;
                    bary[c] = 1f;
                    bary += hide;
                    outBary[write] = bary;
                    outTriangles[s][t + c] = write;
                }
            }
        }

        mesh.Clear();
        mesh.indexFormat = vertexCount > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = outVertices;
        if (hasNormals) mesh.normals = outNormals;
        if (hasUvs) mesh.uv = outUvs;
        mesh.SetUVs(2, outBary);
        mesh.subMeshCount = subMeshCount;
        for (int s = 0; s < subMeshCount; s++)
            mesh.SetTriangles(outTriangles[s], s);
        if (!hasNormals) mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private static bool IsFeatureEdge(List<int> faces, int self, List<Vector3> faceNormals, float cosThreshold)
    {
        if (faces.Count == 1)
            return true; // open boundary / outline
        foreach (int other in faces)
        {
            if (other != self && Vector3.Dot(faceNormals[self], faceNormals[other]) < cosThreshold)
                return true;
        }
        return false;
    }

    private static long EdgeKey(int a, int b)
    {
        return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }
}
