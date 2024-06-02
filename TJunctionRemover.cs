#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.ProBuilder;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using EditorUtility = UnityEditor.EditorUtility;
using Math = UnityEngine.ProBuilder.Math;

[ExecuteInEditMode]
public class TJunctionRemover : MonoBehaviour
{
    private static Vertex CurrentOffendingVertexBeingChecked;
    // private static List<Vertex> VERTICES; 
    // private static List<SharedVertex> SHARED_VERTICES;
    // private static List<Face> FACES;
    // private static HashSet<Vector3> SEEN_VERTS;
    // private static HashSet<Edge> SEEN_EDGES;
    public HashSet<Vector3> _erroredVerts { get; private set; }
    private GameObject _gameObject;
    public SimpleTuple<int, int> BulkIAndLengthStatus;
    
    // private void NullAllLists()
    // {
    //     // VERTICES = null;
    //     // SHARED_VERTICES = null;
    //     // FACES = null;
    //     // SEEN_VERTS = null;
    //     // SEEN_EDGES = null;
    //     _erroredVerts = null;
    // }
    
    [Tooltip("The arbitrary score to consider TJunction. If Custom, you can edit this.")]
    public float TJunctionTolerance = 0.001f;
    public enum TJunctionToleranceOptionsEnum
    {
        Custom,
        Epsilon,
        Stricter,
        Normal,
        SlightlyLenient,
        Lenient,
        VeryLenient,
    }
    [Tooltip("The arbitrary score to consider TJunction.\nBecause an offending vertex is checked if it is already connected to the overlapped edge, it should be ok for Lenient. Else, lower the tolerance.")]
    public TJunctionToleranceOptionsEnum TJunctionToleranceOption = TJunctionToleranceOptionsEnum.Lenient;
    public enum TJunctionSearchModeEnum
    {
        Greedy,
        AllEdgesEachVert
    }
    [Tooltip("On TJunction offending Vert:\n- Pick the first edge that is being overlapped. (Should get the job done)\n- O(n) all edges, and pick the closest edge. (To try and clean up stragglers)")]
    public TJunctionSearchModeEnum TJunctionSearchMode = TJunctionSearchModeEnum.Greedy;
    
    public enum TJunctionFixAlgorithmEnum
    {
        SubdivideEdge,
        // ConnectEdge,
    }
    public TJunctionFixAlgorithmEnum TJunctionFixAlgorithm = TJunctionFixAlgorithmEnum.SubdivideEdge;
    
    [Tooltip("Passes for O(verts). 1 should be enough.")] public int Passes = 1;
    
    [Tooltip("aka Remove Doubles.\n0.1 gives false positives\n0.01 is sweet spot\n0.001 misses some")]public float WeldDistance = 0.01f;

    public TJunctionRemover Backup;
    
    public TJunctionCases DoSelect(ProBuilderMesh pbm, float tolerance = float.Epsilon)
    {
        var timeStarted = Time.realtimeSinceStartup;
        
        pbm.WeldVertices(pbm.faces.SelectMany(x => x.indexes), WeldDistance);
        MeshValidation.RemoveUnusedVertices(pbm);
        
        var VERTICES = pbm.GetVertices();
        var SHARED_VERTICES = pbm.sharedVertices;
        var FACES = pbm.faces;
        var SEEN_EDGES = new HashSet<Edge>(Mathf.RoundToInt(FACES.Count * 7.77777f));
    
        //TJunctionCases
        List<int> Cases_Vertex = new(SHARED_VERTICES.Count/2);
        List<Edge> Case_Edge = new(Cases_Vertex.Count);
        List<Face> Cases_Face = new(Case_Edge.Count);
        
        Debug.Log("Vertices.Count: " + VERTICES.Length);
        Debug.Log("SharedVertices.Count: " + SHARED_VERTICES.Count);
        Debug.Log("Faces.Count: " + FACES.Count);
        Debug.Log("O(n^2): " + (SHARED_VERTICES.Count * FACES.Count));
        
        for (var i = 0; i < SHARED_VERTICES.Count; i++)
        {
            if (EditorUtility.DisplayCancelableProgressBar(_gameObject.name, i + " / " + SHARED_VERTICES.Count, i / (1f * SHARED_VERTICES.Count)))
            {
                EditorUtility.ClearProgressBar();
                Debug.Log("Cancelled: " + (Time.realtimeSinceStartup - timeStarted));
                return new();
            }
            
            var currVertexIndex = SHARED_VERTICES[i][0];
            var vertex = VERTICES[currVertexIndex];
            
            SEEN_EDGES.Clear();
            
            //get list of neighboring faces for offending vert
            List<Face> offendingVertNeighborFaces = FindFacesAttachedToVertexPosition(pbm, vertex, VERTICES);
            
            var isFoundAsTJunction = false;
            foreach (var face in FACES.TakeWhile(face => !isFoundAsTJunction)) 
            {
                //skip: false positive, faces are connected already
                if (offendingVertNeighborFaces.Any(offendingVertNeighborFace => offendingVertNeighborFace == face)) continue;
                
                foreach (var edge in face.edges)
                {
                    //skip: same vert (because edges doesn't do sharedVertices) 
                    if (VERTICES[edge.a].position == vertex.position || VERTICES[edge.b].position == vertex.position) continue;
                    
                    //skip: seenEdges
                    if (SEEN_EDGES.Contains(edge)) continue;
                    SEEN_EDGES.Add(edge);
                    
                    if (CheckIfPointWithinLineSegment(VERTICES[edge.a].position, VERTICES[edge.b].position, vertex.position, tolerance))
                    {
                        //new case
                        // Debug.Log("Found TJunction @ " + currVertexIndex + " " + Vertices[currVertexIndex].position);
                        Cases_Vertex.Add(currVertexIndex);
                        Case_Edge.Add(edge);
                        Cases_Face.Add(face);
                        
                        isFoundAsTJunction = true; //sentinel met
                        break;
                    }
                }
            }
        }
        
        pbm.SetSelectedVertices(Cases_Vertex);
        ProBuilderEditor.selectMode = SelectMode.Vertex;
        ProBuilderEditor.Refresh();
        
        Debug.Log("Vertices Highlighted: " + Cases_Vertex.Count+"/"+SHARED_VERTICES.Count);
        Debug.Log("Finished in: " + (Time.realtimeSinceStartup - timeStarted));

        CurrentOffendingVertexBeingChecked = null;
        VERTICES = null;
        SHARED_VERTICES = null;
        FACES = null;
        
        EditorUtility.ClearProgressBar();
        return new TJunctionCases(Cases_Vertex, Case_Edge, Cases_Face);
    }

    /// <summary>
    /// Fix TJunctions
    /// </summary>
    /// <param name="pbm"></param>
    /// <param name="SEEN_VERTS"></param>
    /// <param name="verticesToFixIndex"></param>
    /// <returns>if still needs to keep going</returns>
    private bool DoFix(ProBuilderMesh pbm, HashSet<Vector3> SEEN_VERTS, int verticesToFixIndex = -1)
    {
        var VERTICES = pbm.GetVertices();
        var SHARED_VERTICES = pbm.sharedVertices;
        var FACES = pbm.faces;
        var SEEN_EDGES = new HashSet<Edge>(Mathf.RoundToInt(FACES.Count * 7.77777f));

        if (verticesToFixIndex == -1)
        {
            for (var i = 0; i < SHARED_VERTICES.Count; i++)
            {
                var sharedVertex = SHARED_VERTICES[i];
                var currOffendingVertexIndex = sharedVertex[0];
                var offendingVertex = VERTICES[currOffendingVertexIndex];
                CurrentOffendingVertexBeingChecked = offendingVertex;
            
                //skip: already checked
                if (SEEN_VERTS.Contains(offendingVertex.position)) continue; 
                SEEN_VERTS.Add(offendingVertex.position);
            
                //skip: has caused error before
                if (_erroredVerts.Contains(offendingVertex.position)) continue;
                
                //seenEdges
                SEEN_EDGES.Clear();
    
                //progress
                var sb = new StringBuilder();
                sb.Append(gameObject.name.Substring(0,32));
                sb.Append(" ");
                sb.Append(BulkIAndLengthStatus.item1);
                sb.Append(" / ");
                sb.Append(BulkIAndLengthStatus.item2);
                var title = sb.ToString();
                sb.Clear();
                sb.Append("Verts Checked: ");
                sb.Append(SEEN_VERTS.Count);
                sb.Append(" (");
                sb.Append(i);
                sb.Append(" / ");
                sb.Append(SHARED_VERTICES.Count);
                sb.Append(")");
                if (EditorUtility.DisplayCancelableProgressBar(title, sb.ToString(), 
                        i / (1f * SHARED_VERTICES.Count)))
                {
                    EditorUtility.ClearProgressBar();
                    // BackupRestore();
                    Debug.Log("Cancelled");
                    return true;
                }
                
                //O(n^2)
                if (DoFix1(pbm, offendingVertex, currOffendingVertexIndex, VERTICES, FACES, SEEN_EDGES)) return false;
            }
        }
        else
        {
            var offendingVertex = VERTICES[verticesToFixIndex];
            DoFix1(pbm, offendingVertex, verticesToFixIndex, VERTICES, FACES, SEEN_EDGES);
            return true;
        }

        VERTICES = null;
        SHARED_VERTICES = null;
        FACES = null;
        CurrentOffendingVertexBeingChecked = null;
        
        EditorUtility.ClearProgressBar();
        return true;
    }

    private bool DoFix1(ProBuilderMesh pbm, Vertex offendingVertex, int currOffendingVertexIndex, Vertex[] VERTICES, IList<Face> FACES, HashSet<Edge> SEEN_EDGES)
    {
        var tjCases = new List<TJunctionCase>(8);
        
        //get list of neighboring faces for offending vert
        List<Face> offendingVertNeighborFaces = FindFacesAttachedToVertexPosition(pbm, offendingVertex, VERTICES);
        
        foreach (var overlappedFace in FACES)
        {
            // //skip: face area = 0; //TODO remove, just use Delete0AreaFaces first or u trolling!
            // if (overlappedFace.indexes.Count % 3 != 0) throw new Exception("overlappedFace Count % 3 != 0: " + overlappedFace.indexes.Count);
            // var areaSum = 0f;
            // for (int i = 0; i < overlappedFace.indexes.Count; i += 3)
            // {
            //     var area = GetAreaOfFace(
            //         Vertices[overlappedFace.indexes[i]].position,
            //         Vertices[overlappedFace.indexes[i + 1]].position,
            //         Vertices[overlappedFace.indexes[i + 2]].position); 
            //
            //     if (Mathf.Approximately(area, 0f) || area <= 0f) break;
            //     areaSum += area;
            // }
            // if (Mathf.Approximately(areaSum, 0) || areaSum <= 0) continue;
            
            //skip: false positive, faces are connected already
            if (offendingVertNeighborFaces.Any(offendingVertNeighborFace => offendingVertNeighborFace == overlappedFace)) continue;
            
            foreach (var overlappedEdge in overlappedFace.edges)
            {
                //skip: same vert
                if (VERTICES[overlappedEdge.a].position == offendingVertex.position || VERTICES[overlappedEdge.b].position == offendingVertex.position) continue;
                
                //skip: same edge
                if (SEEN_EDGES.Contains(overlappedEdge)) continue;
                SEEN_EDGES.Add(overlappedEdge);
                
                //skip: not within line segment (TJunction)
                // if (!CheckIfPointWithinLineSegment(Vertices[overlappedEdge.a].position, Vertices[overlappedEdge.b].position, offendingVertex.position, tolerance)) continue;
                var tJunctionScore = FindScoreOfPointWithinLineSegment(VERTICES[overlappedEdge.a].position, VERTICES[overlappedEdge.b].position, offendingVertex.position);
                if (tJunctionScore > TJunctionTolerance) continue;
                
                //Do
                var overlappedFaceUniqueVertices = new List<Vertex>(3);
                overlappedFaceUniqueVertices.AddRange(overlappedFace.distinctIndexes.Select(distinctIndex => VERTICES[distinctIndex]));
                    
                var tJunctionCase = new TJunctionCase
                {
                    TJunctionScore = tJunctionScore,
                    Offending_VertexIndex = currOffendingVertexIndex,
                    Offending_Vertex = VERTICES[currOffendingVertexIndex],
                    Overlapped_Edge = overlappedEdge,
                    Overlapped_EdgeA = VERTICES[overlappedEdge.a],
                    Overlapped_EdgeB = VERTICES[overlappedEdge.b],
                    Overlapped_Face = overlappedFace,
                    Overlapped_FaceUniqueVertices = overlappedFaceUniqueVertices,
                };
                tjCases.Add(tJunctionCase);

                //searchMode
                if (TJunctionSearchMode == TJunctionSearchModeEnum.Greedy) break;
            }
            
            //searchMode
            if (TJunctionSearchMode == TJunctionSearchModeEnum.Greedy && tjCases.Count > 0) break;
        }

        //FAILED
        if (tjCases.Count == 0) return false;
        
        //SUCCESS
        var tjCase = tjCases.First();
        foreach (var curr in tjCases)
            if (curr.TJunctionScore < tjCase.TJunctionScore)
                tjCase = curr;

        switch (TJunctionFixAlgorithm)
        {
            case TJunctionFixAlgorithmEnum.SubdivideEdge:
                FixTJunction_SubdivideEdge(pbm, tjCase, VERTICES);
                break;
            // case TJunctionFixAlgorithmEnum.ConnectEdge:
            //     FixTJunction_ConnectEdges(pbm, tjCase);
            //     break;
        }
        
        return true;
    }

    private static void FixTJunction_SubdivideEdge(ProBuilderMesh pbm, TJunctionCase tJunctionCase, IList<Vertex> VERTICES)
    {
        var offendedVertex = VERTICES[tJunctionCase.Offending_VertexIndex];
        
        Face appendedFace;
        try
        {
            appendedFace = pbm.AppendVerticesToFace(tJunctionCase.Overlapped_Face, new []{offendedVertex.position}, false); //UV0 destructive (but fixable!)
            // appendedFace = pbm.AppendVerticesToFace(tJunctionCase.Overlapped_Face, new []{offendedVertex.position}, true); //UV0 non-destructive (but can fail...)
        }
        catch (Exception e)
        {
            throw new Exception("AppendVerticesToFace failed." +
                                "\n" + e.Message + 
                                "\n" + e.StackTrace);
        }
        
        // List<Edge> appendedEdges = pbm.AppendVerticesToEdge(tJunctionCase.Overlapped_Edge, 1);

        //new verts
        VERTICES = pbm.GetVertices().ToList();
        
        //find new vert
        int newVertIndex = -1;
        Vertex newVert = null;
        foreach (var appendedFaceDistinctIndex in appendedFace.distinctIndexes)
        {
            var appendedFaceCurrVertex = VERTICES[appendedFaceDistinctIndex];
        
            var isNewVert = true;
            foreach (var faceVert in tJunctionCase.Overlapped_FaceUniqueVertices)
            {
                //is appendedFaceCurrVertex actually just the old one?
                if (faceVert.position == appendedFaceCurrVertex.position)
                {
                    isNewVert = false;
                    break;
                }
            }
        
            //success?
            if (!isNewVert) continue;
            
            //save and break loop
            newVertIndex = appendedFaceDistinctIndex;
            newVert = appendedFaceCurrVertex;
            break;
        }
        // for (int i = 0; i < 2; i++) //one any of the appendedEdge will do. we just need the new vert index.
        // {
        //     var appendedEdgeDistinctIndex = i == 0 ? appendedEdges[0].a : appendedEdges[0].b;
        //     var appendedEdgeCurrVertex = Vertices[appendedEdgeDistinctIndex];
        //
        //     var isNewVert = true;
        //     foreach (var faceVert in tJunctionCase.Overlapped_FaceUniqueVertices)
        //     {
        //         //is appendedFaceCurrVertex actually just the old one?
        //         if (faceVert.position == appendedEdgeCurrVertex.position)
        //         {
        //             isNewVert = false;
        //             break;
        //         }
        //     }
        //
        //     //success?
        //     if (!isNewVert) continue;
        //     
        //     //save and break loop
        //     newVertIndex = appendedEdgeDistinctIndex;
        //     newVert = appendedEdgeCurrVertex;
        //     break;
        // }

        UvNonDestructiveCollapseByEdge(pbm, VERTICES, 
            tJunctionCase.Overlapped_EdgeA.position, tJunctionCase.Overlapped_EdgeB.position, 
            newVertIndex, tJunctionCase.Offending_Vertex.position, tJunctionCase.Overlapped_Face.manualUV);
    }    
    // public static void FixTJunction_ConnectEdges(ProBuilderMesh pbm, TJunctionCase tJunctionCase)
    // {
    //     //connect offending edge with any other edge of the face
    //     var edgesToConnect = new Edge[2] { tJunctionCase.Overlapped_Edge, tJunctionCase.Overlapped_Face.edges.FirstOrDefault(currEdge => currEdge != tJunctionCase.Overlapped_Edge) };
    //     var simpleTuple = pbm.Connect(edgesToConnect);
    //     //new verts
    //     VERTICES = pbm.GetVertices().ToList();
    //
    //     //Check which of the 2 new verts is on the old TJunction edge
    //     Vertex newVertOnTJunctionEdge = new Vertex();
    //     int newVertOnTJunctionEdgeIndex = -1;
    //     foreach (var edge in simpleTuple.item2)
    //     {
    //         //via scoring (because floating point inaccuracy)
    //         var scoreA = FindScoreOfPointWithinLineSegment(tJunctionCase.Overlapped_EdgeA.position, tJunctionCase.Overlapped_EdgeB.position, VERTICES[edge.a].position);
    //         var scoreB = FindScoreOfPointWithinLineSegment(tJunctionCase.Overlapped_EdgeA.position, tJunctionCase.Overlapped_EdgeB.position, VERTICES[edge.b].position);
    //         if (scoreA < scoreB)
    //         {
    //             newVertOnTJunctionEdge = VERTICES[edge.a];
    //             newVertOnTJunctionEdgeIndex = edge.a;
    //         }
    //         else if (scoreA > scoreB)
    //         {
    //             newVertOnTJunctionEdge = VERTICES[edge.b];
    //             newVertOnTJunctionEdgeIndex = edge.b;
    //         }
    //         else //FAILED
    //         {
    //             SEEN_VERTS.Add(VERTICES[edge.a].position);
    //             SEEN_VERTS.Add(VERTICES[edge.b].position);
    //             
    //             // pbm.ClearSelection();
    //             // pbm.SetSelectedVertices(new int[] {edge.a, edge.b, tJunctionCase.VertexIndex});
    //             // pbm.Refresh();
    //             
    //             // SceneView.lastActiveSceneView.pivot = vertices1[edge.a].position + pbm.gameObject.transform.position;
    //             // SceneView.lastActiveSceneView.Repaint();
    //             
    //             // ProBuilderEditor.selectMode = SelectMode.Vertex;
    //             // ProBuilderEditor.Refresh();
    //             
    //             EditorUtility.ClearProgressBar();
    //             
    //             throw new Exception("WHAT! Failed to find newVertOnTJunctionEdge, did you weld double verts?!?!?!! SKIPPING!");
    //             // Debug.LogError("WHAT! Failed to find newVertOnTJunctionEdge, did you weld double verts?!?!?!! SKIPPING!");
    //             // return;
    //         }
    //     }
    //     
    //     if (tJunctionCase.Offending_Vertex.position != VERTICES[tJunctionCase.Offending_VertexIndex].position) //TODO is this necessary?
    //     {
    //         Debug.LogWarning("offendedVertex is not in same index, fixing...");
    //                 
    //         var newVertex = FindVertexAndIndexFromVertexPosition(tJunctionCase.Offending_Vertex.position, VERTICES);
    //         if (newVertex.index == -1)
    //             throw new Exception("offendedVertex: FindIndexFromVertexPosition() returned -1!!!! AHHHHH");
    //     
    //         tJunctionCase.Offending_Vertex = newVertex.vertex;
    //         tJunctionCase.Offending_VertexIndex = newVertex.index;
    //     }
    //     if (newVertOnTJunctionEdge.position != VERTICES[newVertOnTJunctionEdgeIndex].position)
    //     {
    //         Debug.LogWarning("newVertOnTJunctionEdge is not in same index, fixing...");
    //         
    //         var newVertex = FindVertexAndIndexFromVertexPosition(newVertOnTJunctionEdge.position, VERTICES);
    //         if (newVertex.index == -1)
    //             throw new Exception("newVertOnTJunctionEdge: FindIndexFromVertexPosition() returned -1!!!! AHHHHH");
    //     
    //         newVertOnTJunctionEdge = newVertex.vertex;
    //         newVertOnTJunctionEdgeIndex = newVertex.index;
    //     }
    //     
    //     //Collapse that vert to the orig offending TJunction vert
    //     // var newIndex = pbm.MergeVertices(new int[2]{tJunctionCase.Offending_VertexIndex, newVertOnTJunctionEdgeIndex}, true);
    //     UvNonDestructiveCollapseByEdge(pbm, VERTICES, 
    //         tJunctionCase.Overlapped_EdgeA.position, tJunctionCase.Overlapped_EdgeB.position, 
    //         newVertOnTJunctionEdgeIndex, tJunctionCase.Offending_Vertex.position, tJunctionCase.Overlapped_Face.manualUV);
    // }
    
    public static void UvNonDestructiveCollapseByEdge(ProBuilderMesh pbm, IList<Vertex> verts, 
        Vector3 edgeVertAPos, Vector3 edgeVertBPos, int vertexToCollapse, Vector3 desiredVertPos, bool isDoUVStitch = true)
    {
        //positions
        var vertexToCollapseCoincidentVerts = pbm.GetCoincidentVertices(new [] { vertexToCollapse });
        // var edgeVertAPos = vertsToList[edgeVertAIndex].position;
        // var edgeVertBPos = vertsToList[edgeVertBIndex].position;
        // var desiredVertPos = vertsToList[desiredVertIndex].position;
        
        //find the actual edge vertices (the ones that connect to offending vertices)
        var facesConnectedToOrigVertex = GetFacesFromVertex(pbm, vertexToCollapseCoincidentVerts);
        Vertex edgeVertAActual = null;
        Vertex edgeVertBActual = null;
        foreach (var face in facesConnectedToOrigVertex)
        {
            foreach (var index in face.distinctIndexes)
            {
                if (edgeVertAActual != null && edgeVertBActual != null) break; //already done

                //find
                var vertOnFace = verts[index];
                if (edgeVertAActual == null && vertOnFace.position == edgeVertAPos)
                {
                    edgeVertAActual = vertOnFace;
                } else if (edgeVertBActual == null && vertOnFace.position == edgeVertBPos)
                {
                    edgeVertBActual = vertOnFace;
                }
            }
        }
        // if (edgeVertAActual == null) throw new Exception("edgeVertAActual == null");
        // if (edgeVertBActual == null) throw new Exception("edgeVertBActual == null");
        // if (vertexToCollapseCoincidentVerts.Count > 2) throw new Exception("offendingVertCoincidentVerts.Count > 2: " + vertexToCollapseCoincidentVerts.Count);
        if (edgeVertAActual == null) return;
        if (edgeVertBActual == null) return;
        if (vertexToCollapseCoincidentVerts.Count > 2) return;

        //Do...
        // foreach (var originalVertCoincidentVertIndex in vertexToCollapseCoincidentVerts) //Requires ProBuilderMesh.cs to be modified to set uv0 & positions directly
        // {
        //     //STITCH
        //     Debug.Log(isDoUVStitch);
        //     if (isDoUVStitch)
        //     {
        //         var tPosDesired = InverseLerp(edgeVertAActual.position, edgeVertBActual.position, desiredVertPos);
        //         var uv0Desired = Vector2.Lerp(edgeVertAActual.uv0, edgeVertBActual.uv0, tPosDesired);
        //         pbm.m_Textures0[originalVertCoincidentVertIndex] = uv0Desired;
        //     }
        //     
        //     //MOVE
        //     pbm.m_Positions[originalVertCoincidentVertIndex] = desiredVertPos;
        // }
        foreach (var originalVertCoincidentVertIndex in vertexToCollapseCoincidentVerts)
        {
            //STITCH
            if (isDoUVStitch)
            {
                var tPosDesired = InverseLerp(edgeVertAActual.position, edgeVertBActual.position, desiredVertPos);
                var uv0Desired = Vector2.Lerp(edgeVertAActual.uv0, edgeVertBActual.uv0, tPosDesired);
                verts[originalVertCoincidentVertIndex].uv0 = uv0Desired;
            }
            
            //MOVE
            verts[originalVertCoincidentVertIndex].position = desiredVertPos;
        }
        pbm.SetVertices(verts, true);
        
        //WELD
        // pbm.WeldVertices(pbm.faces.SelectMany(x => x.indexes), 0.001f);
        var vertsToWeld = FindVerticesAndIndicesFromVertexPosition(desiredVertPos, verts);
        pbm.WeldVertices(vertsToWeld.Select(returnStruct => returnStruct.index), 0.001f);
    }
    /// <summary>
    /// Returns t of Lerp(a,b,t)
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <param name="value"></param>
    /// <returns>t</returns>
    public static float InverseLerp(Vector3 a, Vector3 b, Vector3 value)
    {
        Vector3 AB = b - a;
        Vector3 AV = value - a;
        return Vector3.Dot(AV, AB) / Vector3.Dot(AB, AB);
    }

    public static FindIndexFromVertexPositionReturnStruct FindVertexAndIndexFromVertexPosition(Vector3 vertexPos, IEnumerable<Vertex> vertices)
    {
        var enumerable = vertices.ToList();
        for (var i = 0; i < enumerable.Count; i++)
        {
            var curr = enumerable[i];
            if (curr.position == vertexPos) //WHY THIS REQUIRES DOUBLES weld!
                return new FindIndexFromVertexPositionReturnStruct(curr, i); //SUCCESS
        }
        return new FindIndexFromVertexPositionReturnStruct(new Vertex(), -1); //FAILED
    }
    public static List<FindIndexFromVertexPositionReturnStruct> FindVerticesAndIndicesFromVertexPosition(Vector3 vertexPos, IEnumerable<Vertex> vertices)
    {
        var result = new List<FindIndexFromVertexPositionReturnStruct>(16);
        var enumerable = vertices.ToList();
        for (var i = 0; i < enumerable.Count; i++)
        {
            var curr = enumerable[i];
            if (curr.position == vertexPos)
                result.Add(new FindIndexFromVertexPositionReturnStruct(curr, i));
        }
        return result;
    }
    public struct FindIndexFromVertexPositionReturnStruct
    {
        public Vertex vertex;
        public int index;
        
        public FindIndexFromVertexPositionReturnStruct(Vertex vertex, int index)
        {
            this.vertex = vertex;
            this.index = index;
        }
    }
    
    public struct TJunctionCases
    {
        public TJunctionCases(List<int> casesVertex, List<Edge> casesEdge, List<Face> casesFace)
        {
            Cases_Vertex = casesVertex;
            Cases_Edge = casesEdge;
            Cases_Face = casesFace;
        }

        public List<int> Cases_Vertex;
        public List<Edge> Cases_Edge;
        public List<Face> Cases_Face;
    }
    
    public struct TJunctionCase
    {
        public float TJunctionScore;
        
        public int Offending_VertexIndex;
        public Vertex Offending_Vertex;
        // public Face Offending_Face;
        // public Edge Offending_Edge;
        // public Face Offending_FaceToStitchWith;
        
        public Edge Overlapped_Edge;
        public Vertex Overlapped_EdgeA;
        public Vertex Overlapped_EdgeB;
        public Face Overlapped_Face;
        public IEnumerable<Vertex> Overlapped_FaceUniqueVertices;
        // public Face Overlapped_FaceToStitchWith;
    }
    
    //https://stackoverflow.com/questions/328107/how-can-you-determine-a-point-is-between-two-other-points-on-a-line-segment#:~:text=Check%20if%20the%20cross%20product,distance%20between%20a%20and%20b.
    // private bool IsCBetweenAB(Vector3 a, Vector3 b, Vector3 c)
    // {
    //     // Debug.Log("IsCBetweenAB: " + a + " " + b + " " + c);
    //     // //crossproduct (on line?)
    //     // var crossproduct = (c.y - a.y) * (b.x - a.x) - (c.x - a.x) * (b.y - a.y);
    //     // Debug.Log("IsCBetweenAB crossproduct: " + crossproduct);
    //     // //compare versus epsilon for floating point values, or != 0 if using integers
    //     // // if (!Mathf.Approximately(crossproduct, 0)) 
    //     // //     return false;
    //     // if (Mathf.Abs(crossproduct) > Mathf.Epsilon) 
    //     //     return false;
    //     //
    //     // //dotproduct (on line?)
    //     // var dotproduct = (c.x - a.x) * (b.x - a.x) + (c.y - a.y) * (b.y - a.y);
    //     // Debug.Log("IsCBetweenAB dotproduct: " + dotproduct);
    //     // if (dotproduct < 0) return false;
    //     //
    //     // //squaredlengthba (between line segment?)
    //     // var squaredlengthba = (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y);
    //     // Debug.Log("IsCBetweenAB squaredlengthba: " + squaredlengthba);
    //     // if (dotproduct > squaredlengthba) return false;
    //     //
    //     // //PASSED
    //     // return true;
    //
    //     return Vector3.Dot( (b-a).normalized , (c-b).normalized ) < TOLERANCE && 
    //            Vector3.Dot( (a-b).normalized , (c-a).normalized ) < TOLERANCE;
    // } 
    public static bool CheckIfPointWithinLineSegment(Vector3 start, Vector3 end, Vector3 point, float tolerance = float.Epsilon)
    {
        return FindScoreOfPointWithinLineSegment(start, end, point) < tolerance;
    }

    //https://gamedev.stackexchange.com/questions/172001/shortest-distance-to-chain-of-line-segments
    //https://forum.unity.com/threads/how-to-check-a-vector3-position-is-between-two-other-vector3-along-a-line.461474/
    public static float FindScoreOfPointWithinLineSegment(Vector3 start, Vector3 end, Vector3 point)
    {
        //ignore if vertex lies on edge.a & edge.b
        if (point == start || point == end) return float.PositiveInfinity;
        
        var wander = point - start;
        var span = end - start;

        // Compute how far along the line is the closest approach to our point.
        float t = Vector3.Dot(wander, span) / span.sqrMagnitude;

        // Restrict this point to within the line segment from start to end.
        t = Mathf.Clamp01(t);

        Vector3 nearest = start + t * span;
        var result = (nearest - point).magnitude;

        return !float.IsNaN(result) ? result : float.PositiveInfinity;
    }

    public static float CheckTJunctionAngle(ProBuilderMesh pbm, List<Vertex> pbmVertices, Vertex offendingVertex, Face overlappingFace, int currOffendingVertexIndex = -1)
    {
        var offendingVertexAttachedFaces = FindFacesAttachedToVertexPosition(pbm, offendingVertex, pbmVertices);
        if (currOffendingVertexIndex != -1 && offendingVertexAttachedFaces.Count < 1)
        {
            pbm.SetSelectedVertices(new []{currOffendingVertexIndex});
            ProBuilderEditor.selectMode = SelectMode.Vertex;
            ProBuilderEditor.Refresh();
            throw new Exception("CheckTJunctionAngle (offendingVertexAttachedFaces.Count < 1) !!!");
        }
        
        var offendingVertexAttachedFacesAvgNormal = FindAvgAngleOfFaces(pbm, offendingVertexAttachedFaces);
        
        return Vector3.Angle(offendingVertexAttachedFacesAvgNormal, FindFaceNormal(pbm, overlappingFace));
    }

    public static Vector3 FindAvgAngleOfFaces(ProBuilderMesh pbm, List<Face> faces)
    {
        var results = new Vector3[faces.Count];

        //find
        for (var i = 0; i < faces.Count; i++) results[i] = FindFaceNormal(pbm, faces[i]);

        //avg
        var result = Vector3.zero;
        foreach (var normal in results)
        {
            result += normal / results.Length;
        }

        return result;
    }

    public static List<Face> FindFacesAttachedToVertexPosition(ProBuilderMesh pbm, Vertex vertex, IList<Vertex> vertices)
    {
        var result = new List<Face>(16);
        result.AddRange(from face in pbm.faces from faceVertexIndex in face.indexes where vertices[faceVertexIndex].position == vertex.position select face);
        return result;
    }

    public static float FindAngleBetween2FaceNormals(ProBuilderMesh pbm, Face f1, Face f2)
    {
        var f1N = FindFaceNormal(pbm, f1);
        var f2N = FindFaceNormal(pbm, f2);
        return Vector3.Angle(f1N, f2N);
    }

    public static Vector3 FindFaceNormal(ProBuilderMesh pbm, Face face)
    {
        return Math.NormalTangentBitangent(pbm, face).normal;
    }

    public static List<SimpleTuple<Face, Edge>> FindUVStitchedFaces(ProBuilderMesh pbm, List<Vertex> verts, Face overlappedFace, bool isStopOnFoundOne = false)
    {
        var result = new List<SimpleTuple<Face, Edge>>(4);

        //find neighbors
        var neighboringFaces = FindNeighboringFaces(pbm, overlappedFace);
        //find edges that overlap
        foreach (var neighboringFace in neighboringFaces)
        {
            var wings = WingedEdge.GetWingedEdges(pbm, new [] { overlappedFace, neighboringFace });
            var sharedEdge = wings.FirstOrDefault(x => x.face == overlappedFace && x.opposite != null && x.opposite.face == neighboringFace);
            if (sharedEdge == null) continue;

            if ((verts[sharedEdge.edge.local.a].uv0 == verts[sharedEdge.opposite.edge.local.a].uv0 &&
                 verts[sharedEdge.edge.local.b].uv0 == verts[sharedEdge.opposite.edge.local.b].uv0) ||
                (verts[sharedEdge.edge.local.a].uv0 == verts[sharedEdge.opposite.edge.local.b].uv0 &&
                 verts[sharedEdge.edge.local.b].uv0 == verts[sharedEdge.opposite.edge.local.a].uv0))
            {
                //SUCCESS!
                result.Add(new SimpleTuple<Face, Edge>(neighboringFace, sharedEdge.edge.local));
                if (isStopOnFoundOne) break;
            }
        }

        return result;
    }
    
    public static List<Face> FindNeighboringFaces(ProBuilderMesh pbm, Face face)
    {
        var neighbors = new List<Face>(4);
        foreach (var edge in face.edges)
        {
            var tempNeighbors = new List<Face>(4);
            ElementSelection.GetNeighborFaces(pbm, edge, tempNeighbors);
            neighbors.AddRange(tempNeighbors.Where(face1 => face != face1));
        }

        return neighbors;
    }
    
    // public static void SetVertexUV0Directly(ProBuilderMesh pbm, int vertexIndex, Vector4 uv0)
    // {
    //     //Requires unlocked ProBuilder
    //     pbm.m_Textures0[vertexIndex] = uv0;
    // }
    //
    // public static void SetVertexPositionDirectly(ProBuilderMesh pbm, int vertexIndex, Vector3 position)
    // {
    //     //Requires unlocked ProBuilder
    //     pbm.m_Positions[vertexIndex] = position;
    // }

    //Requires unlocked ProBuilder
    // [CanBeNull]
    // public static Vertex GetCorrectVertexFromFace(ProBuilderMesh pbm, List<Vertex> vertices, Vertex vertex, Face face)
    // {
    //     foreach (var index in face.indexes)
    //     {
    //         if (pbm.m_Positions[index] == vertex.position) return vertices[index];
    //     }
    //     return null;
    // }

    public static List<Face> GetFacesFromVertex(ProBuilderMesh pb, IEnumerable<int> indexes)
    {
        List<Face> faces = new List<Face>();

        foreach (Face f in pb.faces)
        {
            foreach (int i in f.distinctIndexes)
            {
                if (indexes.Contains(i))
                {
                    faces.Add(f);
                    break;
                }
            }
        }

        return faces;
    }
    
    public float GetAreaOfFace(Vector3 pt1, Vector3 pt2, Vector3 pt3)
    {
        // Debug.Log("GetAreaOfFace: " + pt1 + pt2 + pt3);
    
        float a = Vector3.Distance(pt1, pt2);
        // Debug.Log("a: " + a);
    
        float b = Vector3.Distance(pt2, pt3);
        // Debug.Log("b: " + b);
    
        float c = Vector3.Distance(pt3, pt1);
        // Debug.Log("c: " + c);
    
        float s = (a + b + c) / 2;
        // Debug.Log("s: " + s);
    
        float area = Mathf.Sqrt(s * (s - a) * (s - b) * (s - c));
        // Debug.Log("Area: " + area);
    
        return area;
    }

    // private void OnDrawGizmos()
    // {
    //     //Face normals
    //     var pbm = GetComponent<ProBuilderMesh>();
    //     var faces = pbm.faces;
    //     Gizmos.color = Color.red;
    //     foreach (var face in faces)
    //     {
    //         var pos = HandleUtility.GetActiveElementPosition(pbm, new Face[1] {face});
    //         // var rot = HandleUtility.GetFaceRotation(pbm, face);
    //         var rot = Math.NormalTangentBitangent(pbm, face);
    //         
    //         Gizmos.DrawRay(pos, rot.normal);
    //     }
    // }

    public void DoForSelf(bool isRevertBackupOnFail = true, bool isForceRecursiveErrorMode = false)
    {
        //stats begin
        var timeStarted = Time.realtimeSinceStartup;
        
        Debug.Log("---------------------");
        
        //setup
        if (!TryGetComponent<ProBuilderMesh>(out var pbm)) return;
        
        //backup
        BackupCreate();
        
        // //Undo Start //TODO doesnt work
        // var undoName = "TJunction On " + pbm.gameObject.name;
        // Undo.IncrementCurrentGroup();
        // var groupIndex = Undo.GetCurrentGroup();
        // Undo.SetCurrentGroupName(undoName);
        // Undo.RecordObject(pbm.gameObject, undoName);

        //reset ErroredVerts or continue from prev?
        _erroredVerts ??= new HashSet<Vector3>(16);
        if (_erroredVerts.Count == 0)
            Debug.Log("ErroredVerts.Count: " + _erroredVerts.Count);
        else
            Debug.LogWarning("ErroredVerts.Count: " + _erroredVerts.Count);
        
        //DO!!!!!
        var isDone = false;
        var collapseCount = 0;
        try
        {
            for (int i = 0; i < Passes; i++)
            {
                var SEEN_VERTS = new HashSet<Vector3>(Mathf.RoundToInt(pbm.GetVertices().Length * 1.3f));
                while (!isDone)
                {
                    isDone = DoFix(pbm, SEEN_VERTS);
                    if (!isDone) collapseCount++;
                }
            }
        }
        catch (Exception e)
        {
            //Bruh, something happened.
            Debug.LogError(e);
            
            //remember
            _erroredVerts.Add(CurrentOffendingVertexBeingChecked.position);
            
            //give remembered to backup to not make same mistake
            if (!isRevertBackupOnFail)
            {
                EditorUtility.DisplayDialog("TJunctionRemover Error!", gameObject.name + " failed TJunction Removal!", "Ok");
            }
            else
            {
                var backupRestore = BackupRestore(true);
                backupRestore._erroredVerts = _erroredVerts;

                if (isForceRecursiveErrorMode || EditorUtility.DisplayDialog("TJunctionRemover Error!", gameObject.name + " failed TJunction Removal!", "Retry skipping bad vert", "Stop"))
                    backupRestore.DoForSelf(); //recursive

                DestroyImmediate(gameObject);
            }
            
            EditorUtility.ClearProgressBar();
            return;
        }
        
        //cleanup
        // NullAllLists();
        _erroredVerts.Clear();
        
        //mesh cleanup
        pbm.ToTriangles(pbm.faces);
        pbm.ToMesh();
        pbm.Refresh();
        pbm.Optimize();

        // //Undo End
        // Undo.CollapseUndoOperations(groupIndex);
        
        //stats end
        Debug.Log("Collapsed: " + collapseCount);
        Debug.Log("Done in: " + (Time.realtimeSinceStartup - timeStarted));
        EditorUtility.ClearProgressBar();
        
        ProBuilderEditor.Refresh();
    }

    public void DoForSelf1Vert()
    {
        //stats begin
        var timeStarted = Time.realtimeSinceStartup;
        
        Debug.Log("---------------------");
            
        //backup
        BackupCreate();

        //setup
        var pbm = GetComponent<ProBuilderMesh>();
        pbm.ToTriangles(pbm.faces);

        //DO!!!!!
        var SEEN_VERTS = new HashSet<Vector3>(0);
        _erroredVerts = new HashSet<Vector3>(0);
        foreach (var selectedVertex in pbm.selectedVertices) DoFix(pbm, SEEN_VERTS, selectedVertex);

        //cleanup
        // NullAllLists();
        _erroredVerts.Clear();
            
        //mesh cleanup
        pbm.ToTriangles(pbm.faces);
        pbm.ToMesh();
        pbm.Refresh();
        pbm.Optimize();
        ProBuilderEditor.Refresh();
            
        //stats end
        Debug.Log("Done in: " + (Time.realtimeSinceStartup - timeStarted));
        EditorUtility.ClearProgressBar();
            
        ProBuilderEditor.Refresh();
    }

    public void OnValidate()
    {
        TJunctionTolerance = TJunctionToleranceOption switch
        {
            TJunctionToleranceOptionsEnum.Epsilon => float.Epsilon,
            TJunctionToleranceOptionsEnum.Stricter => 0.0001f,
            TJunctionToleranceOptionsEnum.Normal => 0.001f,
            TJunctionToleranceOptionsEnum.SlightlyLenient => 0.0025f,
            TJunctionToleranceOptionsEnum.Lenient => 0.005f,
            TJunctionToleranceOptionsEnum.VeryLenient => 0.01f,
            _ => TJunctionTolerance
        };
    }
    
    private void Awake()
    {
        _gameObject = gameObject;
    }

    public void BackupCreate()
    {
        BackupDelete();
        Backup = Instantiate(this, transform.parent, true);
        
        var backupPbm = Backup.GetComponent<ProBuilderMesh>();
        backupPbm.MakeUnique();
        backupPbm.ToMesh();
        backupPbm.Refresh();
        
        Backup.transform.SetSiblingIndex(transform.GetSiblingIndex() + 1);
        Backup.gameObject.SetActive(false);
        // Backup.gameObject.hideFlags &= ~HideFlags.DontSaveInBuild;
    }

    public TJunctionRemover BackupRestore(bool isOnDestroy = false)
    {
        if (Backup == null) return null;
        
        Backup.gameObject.SetActive(true);
        Selection.activeTransform = Backup.transform;
        // Backup.gameObject.hideFlags = HideFlags.DontSaveInBuild;
        
        if (!isOnDestroy) DestroyImmediate(gameObject);
        
        EditorUtility.ClearProgressBar();
        
        var backup = Backup;
        Backup = null;
        return backup;
    }

    private void OnDestroy()
    {
        BackupRestore(true);
    }

    public bool BackupDelete()
    {
        if (Backup != null)
        {
            DestroyImmediate(Backup.gameObject);
            return true;
        }

        return false;
    }
}

[CustomEditor(typeof(TJunctionRemover))]
[CanEditMultipleObjects]
public class TJunctionRemoverEditor : Editor
{
    // private int currEdge;
    
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        TJunctionRemover myScript = (TJunctionRemover) target;
        
        // myScript._tJunctionToleranceOption = (TJunctionRemover.TJunctionToleranceOptionsEnum)EditorGUILayout.EnumPopup("TJunctionTolerance", myScript._tJunctionToleranceOption);
        // myScript._stitchOption = (TJunctionRemover.StitchOptionEnum)EditorGUILayout.EnumPopup("TJunctionTolerance", myScript._stitchOption);
        // myScript.angleThreshold = EditorGUILayout.Slider("Angel Threshold", myScript.angleThreshold, 0, 360f);

        if (myScript._erroredVerts != null) EditorGUILayout.HelpBox("_errorVerts.Count: " + myScript._erroredVerts.Count, MessageType.None);
        
        if (GUILayout.Button("Select"))
        {
            var pbm = myScript.GetComponent<ProBuilderMesh>();
            Debug.Log("---------------------");
            myScript.DoSelect(pbm, myScript.TJunctionTolerance);
            
            ProBuilderEditor.Refresh();
        }
        
        GUILayout.Space(5f);
        if (GUILayout.Button("Fix Mesh"))
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go.TryGetComponent<TJunctionRemover>(out var tjr)) tjr.DoForSelf();
            }
        }
        if (GUILayout.Button("Fix Mesh (Auto Retry On Error)"))
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go.TryGetComponent<TJunctionRemover>(out var tjr)) tjr.DoForSelf(true, true);
            }
        }
        if (GUILayout.Button("Fix Selected Verts"))
        {
            myScript.DoForSelf1Vert();
        }
        
        GUILayout.Space(5f);
        if (GUILayout.Button("Restore Backup"))
        {
            foreach (var go in Selection.gameObjects)
                if (go.TryGetComponent<TJunctionRemover>(out var tjr))
                    tjr.BackupRestore();
        }        
        if (GUILayout.Button("Delete Backup"))
        {
            foreach (var go in Selection.gameObjects)
                if (go.TryGetComponent<TJunctionRemover>(out var tjr))
                    tjr.BackupDelete();
        }
        
        // GUILayout.Space(15);
        // if (GUILayout.Button("Selected Vertex Info"))
        // {
        //     var pbm = myScript.GetComponent<ProBuilderMesh>();
        //     var v = pbm.selectedVertices[0];
        //     Debug.Log(v + ": " + pbm.GetVertices()[v].position);
        // }
        // if (GUILayout.Button("Convert to AutoUV"))
        // {
        //     var pbm = myScript.GetComponent<ProBuilderMesh>();
        //     
        //     pbm.ToMesh();
        //     UvUnwrapping.SetAutoUV(pbm, pbm.faces, true);
        //     pbm.Refresh();
        //     pbm.Optimize();
        //     
        //     RefreshUVCoordinates();
        // }
    }
}

#else
using UnityEngine;
public class TJunctionRemover : MonoBehaviour
{
    private void Awake()
    {
        Destroy(this);
    }
}
#endif