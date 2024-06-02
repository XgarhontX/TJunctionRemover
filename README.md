# TJunctionRemover
Unity w/ ProBuilder script that resolves mesh t-junctions while preserving UV0.
- Made on Unity 2022.3.13f1 w/ ProBuilder 5.2.2
## Why?
- RealtimeCSG (https://github.com/LogicalError/realtime-CSG-for-unity) generates lots of T-junctions.
- It's all fine until you bake lighting.
- The t-junctions causes Unity UV1 lightmap unwrap to create seams along adjancent faces.
## Usage
- Add TJunctionRemover to ProBuilderMesh containing GameObjects.
- Click fix (can multi-select for bulk, or add TJunctionRemoverBulkController to parent of ProBuilderMeshes).
## Demo
TODO pic or vid
## How it works
- For each vertex to each edge.
- Check if vertex lies on edge.
  - Uses threshold to combat floating point.
  - 2 modes
    - Greedy: Try to fix first found edge meeting threshold.
    - Best: Score all edges meeting threshold and pick the best.
- Add a new vertex on overlapped edge.
  - This can throw exception, vertex will be skipped if so.
- Move new vertex to same position as offending vertex (while fixing UV0 of move).
- Weld new vertex with offending vertex, connecting face and resolving T-junction.
