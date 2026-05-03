using System.Globalization;
using System.Numerics;
using Cast.NET;
using Cast.NET.Nodes;

namespace Arcturus.ModelExportTool;

internal static class ModelExporters
{
  public static void ExportObj(ArcturusModel model, string outputPath, bool flipV)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

    using var sw = new StreamWriter(outputPath, false);

    sw.WriteLine($"# Exported from {Path.GetFileName(model.SourcePath)}");
    sw.WriteLine($"# Meshes: {model.Meshes.Count}");

    int vertexBase = 1;
    int uvBase = 1;
    var nameUseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    bool addBoundaryCaps = model.Format == ArcturusModelFormat.Grsm;

    foreach (var mesh in model.Meshes)
    {
      string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? "mesh" : mesh.Name;
      string uniqueName = MakeUniqueName(meshName, nameUseCounts);
      sw.WriteLine();
      sw.WriteLine($"o {SanitizeObjName(uniqueName)}");
      sw.WriteLine($"g {SanitizeObjName(uniqueName)}");

      foreach (var v in mesh.Vertices)
      {
        var p = ToObjSpace(v, model.Format);
        sw.WriteLine(FormattableString.Invariant($"v {p.X} {p.Y} {p.Z}"));
      }

      foreach (var tv in mesh.TextureVertices)
      {
        var uv = ConvertTexVertexToUv(tv, flipV);
        sw.WriteLine(FormattableString.Invariant($"vt {uv.X} {uv.Y}"));
      }

      List<ArcturusFace> exportFaces = GetExportFaces(mesh, addBoundaryCaps);

      foreach (var f in exportFaces)
      {
        if ((uint)f.Vertex0 >= mesh.Vertices.Count
            || (uint)f.Vertex1 >= mesh.Vertices.Count
            || (uint)f.Vertex2 >= mesh.Vertices.Count)
        {
          continue;
        }

        bool hasUv = mesh.TextureVertices.Count > 0
            && f.Tex0 < mesh.TextureVertices.Count
            && f.Tex1 < mesh.TextureVertices.Count
            && f.Tex2 < mesh.TextureVertices.Count;

        if (hasUv)
        {
          sw.WriteLine($"f {vertexBase + f.Vertex0}/{uvBase + f.Tex0} {vertexBase + f.Vertex1}/{uvBase + f.Tex1} {vertexBase + f.Vertex2}/{uvBase + f.Tex2}");
        }
        else
        {
          sw.WriteLine($"f {vertexBase + f.Vertex0} {vertexBase + f.Vertex1} {vertexBase + f.Vertex2}");
        }
      }

      vertexBase += mesh.Vertices.Count;
      uvBase += mesh.TextureVertices.Count;
    }
  }

  public static void ExportCast(ArcturusModel model, string outputPath, bool flipV)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

    var root = new CastNode(CastNodeIdentifier.Root);
    var modelNode = root.AddNode<ModelNode>();
    modelNode.AddString("n", model.Name);

    foreach (var srcMesh in model.Meshes)
    {
      if (srcMesh.Vertices.Count == 0 || srcMesh.Faces.Count == 0)
      {
        continue;
      }

      var meshNode = modelNode.AddNode<MeshNode>();
      meshNode.Name = string.IsNullOrWhiteSpace(srcMesh.Name) ? "mesh" : srcMesh.Name;

      // Cast face buffer is vertex indices only; UVs are linked by matching vertex stream index.
      // For Arcturus meshes, UV indices are explicit and can diverge from vertex indices.
      // To preserve this in Cast, we de-index into a unified vertex stream first.
        List<ArcturusFace> exportFaces = GetExportFaces(srcMesh, model.Format == ArcturusModelFormat.Grsm);

      BuildUnifiedStreams(srcMesh, flipV,
          out List<Vector3> unifiedPos,
          out List<Vector2> unifiedUv,
          out List<int> faceIndices,
          exportFaces);

      for (int i = 0; i < unifiedPos.Count; i++)
      {
        unifiedPos[i] = ToCastSpace(unifiedPos[i], model.Format);
      }

      meshNode.AddArray("vp", unifiedPos);
      meshNode.UVLayerCount = 1;
      meshNode.AddUVLayer(0, new CastArrayProperty<Vector2>(unifiedUv));

      if (unifiedPos.Count <= byte.MaxValue)
      {
        meshNode.AddArray("f", faceIndices.Select(i => (byte)i).ToList());
      }
      else if (unifiedPos.Count <= ushort.MaxValue)
      {
        meshNode.AddArray("f", faceIndices.Select(i => (ushort)i).ToList());
      }
      else
      {
        meshNode.AddArray("f", faceIndices.Select(i => (uint)i).ToList());
      }
    }

    CastWriter.Save(outputPath, root);
  }

  private static void BuildUnifiedStreams(
      ArcturusMesh mesh,
      bool flipV,
      out List<Vector3> positions,
      out List<Vector2> uvs,
      out List<int> faceIndices,
      IReadOnlyList<ArcturusFace> faces)
  {
    var localPositions = new List<Vector3>();
    var localUvs = new List<Vector2>();
    var localFaceIndices = new List<int>(faces.Count * 3);

    var map = new Dictionary<(int v, int t), int>();

    foreach (var face in faces)
    {
      if ((uint)face.Vertex0 >= mesh.Vertices.Count
          || (uint)face.Vertex1 >= mesh.Vertices.Count
          || (uint)face.Vertex2 >= mesh.Vertices.Count)
      {
        continue;
      }

      AppendCorner(face.Vertex0, face.Tex0);
      AppendCorner(face.Vertex1, face.Tex1);
      AppendCorner(face.Vertex2, face.Tex2);
    }

    void AppendCorner(int vertexIndex, int texIndex)
    {
      if ((uint)vertexIndex >= mesh.Vertices.Count)
      {
        return;
      }

      int clampedTex = (uint)texIndex < mesh.TextureVertices.Count ? texIndex : 0;
      var key = (vertexIndex, clampedTex);

      if (!map.TryGetValue(key, out int unifiedIndex))
      {
        unifiedIndex = localPositions.Count;
        map[key] = unifiedIndex;

        localPositions.Add(mesh.Vertices[vertexIndex]);

        Vector2 uv = mesh.TextureVertices.Count == 0
            ? Vector2.Zero
            : ConvertTexVertexToUv(mesh.TextureVertices[clampedTex], flipV);

        localUvs.Add(uv);
      }

      localFaceIndices.Add(unifiedIndex);
    }

    positions = localPositions;
    uvs = localUvs;
    faceIndices = localFaceIndices;
  }

  private static List<ArcturusFace> GetExportFaces(ArcturusMesh mesh, bool addBoundaryCaps)
  {
    var faces = new List<ArcturusFace>(mesh.Faces.Count);

    foreach (ArcturusFace f in mesh.Faces)
    {
      if ((uint)f.Vertex0 >= mesh.Vertices.Count
          || (uint)f.Vertex1 >= mesh.Vertices.Count
          || (uint)f.Vertex2 >= mesh.Vertices.Count)
      {
        continue;
      }

      faces.Add(f);
    }

    if (addBoundaryCaps)
    {
      AddBoundaryCaps(mesh, faces);
    }

    return faces;
  }

  private static void AddBoundaryCaps(ArcturusMesh mesh, List<ArcturusFace> faces)
  {
    var directed = new List<(ushort a, ushort b, ushort texA, ushort texB)>();
    var edgeCount = new Dictionary<(ushort lo, ushort hi), int>();

    void RegisterEdge(ushort a, ushort b, ushort ta, ushort tb)
    {
      directed.Add((a, b, ta, tb));
      var key = a < b ? (a, b) : (b, a);
      edgeCount.TryGetValue(key, out int count);
      edgeCount[key] = count + 1;
    }

    foreach (ArcturusFace f in faces)
    {
      RegisterEdge(f.Vertex0, f.Vertex1, f.Tex0, f.Tex1);
      RegisterEdge(f.Vertex1, f.Vertex2, f.Tex1, f.Tex2);
      RegisterEdge(f.Vertex2, f.Vertex0, f.Tex2, f.Tex0);
    }

    var boundaryOut = new Dictionary<ushort, List<(ushort next, ushort texAtStart)>>();
    foreach (var e in directed)
    {
      var key = e.a < e.b ? (e.a, e.b) : (e.b, e.a);
      if (!edgeCount.TryGetValue(key, out int count) || count != 1)
      {
        continue;
      }

      if (!boundaryOut.TryGetValue(e.a, out var list))
      {
        list = new List<(ushort next, ushort texAtStart)>();
        boundaryOut[e.a] = list;
      }

      list.Add((e.b, e.texA));
    }

    if (boundaryOut.Count == 0)
    {
      return;
    }

    var visited = new HashSet<(ushort a, ushort b)>();

    foreach ((ushort start, List<(ushort next, ushort texAtStart)> edges) in boundaryOut)
    {
      foreach ((ushort next, ushort texAtStart) in edges)
      {
        if (!visited.Add((start, next)))
        {
          continue;
        }

        var loop = new List<ushort> { start, next };
        var loopTex = new List<ushort> { texAtStart, texAtStart };

        ushort prev = start;
        ushort curr = next;

        while (curr != start)
        {
          if (!boundaryOut.TryGetValue(curr, out var outs))
          {
            break;
          }

          (ushort nextV, ushort nextTex)? picked = null;
          foreach ((ushort candidateNext, ushort candidateTex) in outs)
          {
            if (candidateNext == prev)
            {
              continue;
            }

            if (!visited.Contains((curr, candidateNext)))
            {
              picked = (candidateNext, candidateTex);
              break;
            }
          }

          if (!picked.HasValue)
          {
            break;
          }

          visited.Add((curr, picked.Value.nextV));
          prev = curr;
          curr = picked.Value.nextV;

          if (curr != start)
          {
            loop.Add(curr);
            loopTex.Add(picked.Value.nextTex);
          }
        }

        if (curr != start || loop.Count < 3)
        {
          continue;
        }

        ushort fallbackTex = mesh.TextureVertices.Count > 0 ? loopTex[0] : (ushort)0;
        for (int i = 1; i < loop.Count - 1; i++)
        {
          ushort v0 = loop[0];
          ushort v1 = loop[i + 1];
          ushort v2 = loop[i];

          ushort t0 = mesh.TextureVertices.Count > 0 ? loopTex[0] : fallbackTex;
          ushort t1 = mesh.TextureVertices.Count > 0 ? loopTex[Math.Min(i + 1, loopTex.Count - 1)] : fallbackTex;
          ushort t2 = mesh.TextureVertices.Count > 0 ? loopTex[Math.Min(i, loopTex.Count - 1)] : fallbackTex;

          faces.Add(new ArcturusFace(v0, v1, v2, t0, t1, t2, 0, 0, 0, 0));
        }
      }
    }
  }

  private static Vector2 ConvertTexVertexToUv(Vector3 texVertex, bool flipV)
  {
    // Arcturus stores texture vectors as vec3; in legacy records, X is often sentinel (-1),
    // while Y/Z contain UV values.
    float u = texVertex.Y;
    float v = texVertex.Z;

    if (flipV)
    {
      v = 1.0f - v;
    }

    return new Vector2(u, v);
  }

  private static Vector3 ToObjSpace(Vector3 value, ArcturusModelFormat format)
  {
    // Blender OBJ import check: RSX currently appears vertically inverted without this conversion.
    return format switch
    {
      ArcturusModelFormat.Grsx => new Vector3(value.X, value.Y, -value.Z),
      _ => value
    };
  }

  private static Vector3 ToCastSpace(Vector3 value, ArcturusModelFormat format)
  {
    // Cast scene check: RSX currently appears +90deg around X without this conversion.
    return format switch
    {
      ArcturusModelFormat.Grsx => new Vector3(value.X, value.Z, -value.Y),
      _ => value
    };
  }

  private static string SanitizeObjName(string name)
  {
    var chars = name
        .Select(c => char.IsWhiteSpace(c) ? '_' : c)
        .ToArray();

    return new string(chars);
  }
  private static string MakeUniqueName(string baseName, IDictionary<string, int> counts)
  {
    string key = string.IsNullOrWhiteSpace(baseName) ? "mesh" : baseName;

    if (!counts.TryGetValue(key, out int count))
    {
      counts[key] = 1;
      return key;
    }

    count++;
    counts[key] = count;
    return $"{key}_{count}";
  }
}
