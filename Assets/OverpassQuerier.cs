using UnityEngine;
using UnityEngine.Networking;
using Unity.Mathematics;
using System.Collections;
using CesiumForUnity;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.Splines;
using System.Linq;
using System.Threading;

/// <summary>
/// Queries Overpass Turbo and spawns objects using the response.
/// </summary>
public class OverpassQuerier : MonoBehaviour
{
    /// <summary>
    /// Placeholder prefab to shows spawned railway objects.
    /// </summary>
    public GameObject cuboidPrefab;

    /// <summary>
    /// The tileset used to generate the terrain, sample heights and serve as a parent of spawned objects.
    /// </summary>
    public Cesium3DTileset tileset;

    /// <summary>
    /// The anchor used to spawn objects around. Should normally be a DynamicCamera.
    /// </summary>
    public CesiumGlobeAnchor anchor;

    /// <summary>
    /// A georeference to place spawned objects in the world around the anchor.
    /// </summary>
    public CesiumGeoreference georeference;

    /// <summary>
    /// A list of spawned objects used as a reference to make clearing objects easier.
    /// </summary>
    public List<GameObject> spawnedObjects = new();

    /// <summary>
    /// The ID of the relation to use to populate the world.
    /// </summary>
    public int relationID = 11843297;

    /// <summary>
    /// The number of nodes to spawn per sample.
    /// The lower the node count, the higher the accuracy, with 1 giving maximum accuracy.
    /// </summary>
    public int nodesPerSample = 5;

    /// <summary>
    /// The angle in degrees above which points sampled in the world are considered to be anomalous.
    /// </summary>
    public float gradeDegreeLimit = 6f;

    /// <summary>
    /// The change in grade above which points sampled in the world are considered to be anomalous.
    /// </summary>
    public float gradeDegreeChangeLimit = 1f;

    /// <summary>
    /// Anomalous positions where the grade is above the degree limit.
    /// </summary>
    public HashSet<Vector3> anomalousPositions = new();

    public List<long> stationIdsOrdered = new();
    public List<long> trackIdsOrdered = new();
    public Dictionary<long, JToken> stations = new();
    public Dictionary<long, JToken> tracks = new();

    /// <summary>
    /// Cancellation token source used to cancel running async functions.
    /// Token is created at start and passed through to all async functions.
    /// Async functions then check the token at specified points in runtime.
    /// </summary>
    private CancellationTokenSource queryCancellationToken;

    /// <summary>
    /// Clears any objects and then queries Overpass Turbo, spawning a new set of objects.
    /// </summary>
    public void TriggerQuery()
    {
        ClearObjects();
        queryCancellationToken = new CancellationTokenSource();
        StartCoroutine(QueryOverpass(queryCancellationToken.Token));
    }

    /// <summary>
    /// Clears all currently spawned objects.
    /// </summary>
    public void ClearObjects()
    {
        StopAllCoroutines();
        queryCancellationToken?.Cancel();
        queryCancellationToken?.Dispose();
        queryCancellationToken = null;
        anomalousPositions.Clear();

        foreach (GameObject obj in spawnedObjects)
        {
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
        spawnedObjects.Clear();
        stationIdsOrdered.Clear();
        trackIdsOrdered.Clear();
        stations.Clear();
        tracks.Clear();
    }

    IEnumerator QueryOverpass(CancellationToken token)
    {
        // This query gets stations and tracks near the anchor.
        // string query = $@"[out:json];
        // (
        //   node[""railway""=""station""][""station""!~""subway""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
        //   node[""railway""=""station""][""station""=""subway""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
        //   way[""railway""=""rail""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
        // );
        // out geom;";

        // Populate the world with stations and tracks using a relation.
        string query = $@"[out:json][timeout:25];
        relation({relationID});
		(._;>>;);
        out geom;";

        string encodedQuery = UnityWebRequest.EscapeURL(query);
        Debug.Log(query); // Log what was queried.
        string url = "https://overpass-api.de/api/interpreter?data=" + encodedQuery;

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                yield return new WaitForTask(ProcessJson(webRequest.downloadHandler.text, token));
            }
            else
            {
                Debug.LogError("Error: " + webRequest.error);
            }
        }
    }

    async Task ProcessJson(string jsonString, CancellationToken token)
    {
        JObject jsonObject = JObject.Parse(jsonString);
        JArray elements = (JArray)jsonObject["elements"];
        Debug.Log(elements);

        // Process query data.
        foreach (var element in elements)
        {
            string type = (string)element["type"];
            JObject tags = (JObject)element["tags"];

            switch (type)
            {
                // Use relation data to determine spawn order.
                case "relation":
                    JArray members = (JArray)element["members"];
                    Debug.Log(members);
                    foreach (var member in members)
                    {
                        string memberType = (string)member["type"];
                        string memberRole = (string)member["role"];
                        long memberRef = (long)member["ref"];
                        if (memberType == "node" && memberRole == "stop")
                        {
                            stationIdsOrdered.Add(memberRef);
                        }
                        else if (memberType == "way" && memberRole == "")
                        {
                            trackIdsOrdered.Add(memberRef);
                        }
                    }
                    break;
                // If the relation was processed first, then dictionary keys will already be set.
                case "node":
                    stations.Add((long)element["id"], element);
                    double lat = (double)element["lat"];
                    double lon = (double)element["lon"];
                    // StartCoroutine(SpawnObject(lat, lon, tags));
                    break;
                case "way":
                    tracks.Add((long)element["id"], element);
                    JArray geometry = (JArray)element["geometry"];
                    // SpawnWay(geometry, tags);
                    break;
            }
        }

        // Generate the route after query data has been processed.
        await GenerateSplineWithTerrainHeights(token);
    }

    IEnumerator SpawnObject(double lat, double lon, JObject tags)
    {
        // Calculate the position of the object with the height sampled.
        Task<CesiumSampleHeightResult> task = tileset.SampleHeightMostDetailed(new double3(lon, lat, 1.0));
        yield return new WaitForTask(task);
        CesiumSampleHeightResult result = task.Result;
        if (result.sampleSuccess[0])
        {
            // Spawn the object, adding it to the tracked list of objects and activating it.
            GameObject cuboid = Instantiate(cuboidPrefab, georeference.transform);
            spawnedObjects.Add(cuboid);
            cuboid.SetActive(true);

            // Set the position of the object using the sampled data.
            CesiumGlobeAnchor cuboidAnchor = cuboid.AddComponent<CesiumGlobeAnchor>();
            cuboidAnchor.longitudeLatitudeHeight = result.longitudeLatitudeHeightPositions[0];

            // Set the tileset as the parent to make spawned objects easier to track in the editor.
            cuboid.transform.SetParent(tileset.transform, true);

            // Adjust scale for visibility and print a notice to the console.
            cuboid.transform.localScale = new Vector3(5, 5, 5);
            Debug.Log($"Spawned object at {cuboidAnchor.longitudeLatitudeHeight} with tags: {tags}");
        }
    }

    void SpawnWay(JArray geometry, JObject tags)
    {
        for (int i = 0; i < geometry.Count - 1; i++)
        {
            var node1 = geometry[i];
            var node2 = geometry[i + 1];

            double lat1 = (double)node1["lat"];
            double lon1 = (double)node1["lon"];
            double lat2 = (double)node2["lat"];
            double lon2 = (double)node2["lon"];

            StartCoroutine(SpawnCuboidBetweenPoints(lat1, lon1, lat2, lon2));
        }

        Debug.Log($"Spawned way with {geometry.Count} points and tags: {tags}");
    }

    IEnumerator SpawnCuboidBetweenPoints(double lat1, double lon1, double lat2, double lon2)
    {
        // Set position to midpoint of the two nodes.
        double lon = (lon1 + lon2) / 2;
        double lat = (lat1 + lat2) / 2;

        // Calculate the position of the object with the height sampled.
        Task<CesiumSampleHeightResult> task = tileset.SampleHeightMostDetailed(new double3(lon, lat, 1.0));
        yield return new WaitForTask(task);
        CesiumSampleHeightResult result = task.Result;
        if (result.sampleSuccess[0])
        {
            // Spawn the object, adding it to the tracked list of objects and activating it.
            GameObject cuboid = Instantiate(cuboidPrefab, georeference.transform);
            spawnedObjects.Add(cuboid);
            cuboid.SetActive(true);

            // Set the position of the object using the sampled data.
            CesiumGlobeAnchor cuboidAnchor = cuboid.AddComponent<CesiumGlobeAnchor>();
            cuboidAnchor.longitudeLatitudeHeight = result.longitudeLatitudeHeightPositions[0];
            cuboid.transform.SetParent(tileset.transform, true);

            // Calculate the rotation to align with the track direction.
            // Convert lat/lon to Unity world positions for correct angle calculations.
            Vector3 world1 = ToUnityPosition(georeference, lon1, lat1);
            Vector3 world2 = ToUnityPosition(georeference, lon2, lat2);

            // Compute direction and rotation.
            Vector3 direction = (world2 - world1).normalized;
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

            // Apply rotation.
            cuboid.transform.rotation = Quaternion.Euler(0, angle, 0); // adjust with -90f if needed

            // Scale the cuboid to fit between the points (rough approximation).
            float distance = CalculateDistance(lat1, lon1, lat2, lon2);
            cuboid.transform.localScale = new Vector3(2, 2, distance);

            // Set the tileset as the parent to make spawned objects easier to track in the editor.
            cuboid.transform.SetParent(tileset.transform, true);
        }
    }

    async Task GenerateSplineWithTerrainHeights(CancellationToken token)
    {
        // Create a GameObject to hold the SplineContainer.
        GameObject splineGameObject = new("Spline Container");
        spawnedObjects.Add(splineGameObject);
        var container = splineGameObject.AddComponent<SplineContainer>();

        // Assign the spline container to the train controller.
        var trainController = GetComponent<TrainController>();
        if (trainController != null)
        {
            trainController.splineContainer = container;
        }

        // Create a queue of all nodes along the entire track.
        Queue<WayNode> unsmoothedNodes = new();
        bool lastWasTunnel = false;
        for (int i = 0; i < trackIdsOrdered.Count; i++)
        {
            // Get the geometry of the current way.
            var id = trackIdsOrdered[i];
            var way = tracks[id];
            var wayGeometry = (JArray)way["geometry"];

            // Skip this way if it does not have geometry.
            if (wayGeometry == null || wayGeometry.Count() == 0)
            {
                continue;
            }

            // Determine if the way is a tunnel from the tags.
            bool isTunnel = way["tags"]?["tunnel"]?.ToString() == "yes";

            // Store the geometry of the way to be added to the spline later after height smoothing.
            // Do not store the end node until the last way as this is the same as the start node of the next way.
            int jLimit = i == trackIdsOrdered.Count - 1 ? wayGeometry.Count() : wayGeometry.Count() - 1;
            for (int j = 0; j < jLimit; j++)
            {
                var node = wayGeometry[j];
                double lat = (double)node["lat"];
                double lon = (double)node["lon"];
                // Mark both tunnel nodes and the tunnel exit node as tunnels.
                unsmoothedNodes.Enqueue(new WayNode(lat, lon, isTunnel || j == 0 && lastWasTunnel));
            }
            lastWasTunnel = isTunnel;
        }

        // Flag to update the origin to be at the spline.
        bool setOrigin = false;

        // Process and add all of the track's nodes to the spline.
        List<WayNode> nodesInBatch = new();
        double3 lastSample = new();
        float? lastSlopeDeg = null; // Track slope across samples to aid smoothing.
        while (unsmoothedNodes.Count > 0)
        {
            if (token.IsCancellationRequested) return;

            // Skip samples of tunnel nodes.
            var currentNode = unsmoothedNodes.Peek();
            if (currentNode.IsTunnel)
            {
                nodesInBatch.Add(unsmoothedNodes.Dequeue());
                continue;
            }

            // Get a sample of the first unprocessed node.
            var result = await tileset.SampleHeightMostDetailed(CoordinatesNodeToDouble3(currentNode));
            if (!result.sampleSuccess[0])
            {
                // If unsuccessful, skip to the next few nodes to sample.
                // Possible TODO for future: Skipping nodes without changing the start sample logic means skipped nodes may have poor smoothing.
                for (int j = 0; j < nodesPerSample && unsmoothedNodes.Count > 0; j++)
                {
                    nodesInBatch.Add(unsmoothedNodes.Dequeue());
                }
                continue;
            }
            double3 currentSample = result.longitudeLatitudeHeightPositions[0];

            // Check whether the origin has been set yet.
            if (!setOrigin)
            {
                // Set the origin to just above the first sample and reset the position of the querier (usually attached to the camera).
                georeference.SetOriginLongitudeLatitudeHeight(currentSample[0], currentSample[1], currentSample[2] + 3f);
                transform.position = Vector3.zero;

                // Add a globe anchor and set the georeference as the parent to keep the container's position accurate to the world.
                splineGameObject.AddComponent<CesiumGlobeAnchor>();
                splineGameObject.transform.SetParent(georeference.transform);

                // Update the flag to avoid updating the origin again.
                setOrigin = true;
            }

            // If the last sample has been set, process all unsmoothed nodes.
            // The current slope is returned if successful. Otherwise, null is returned.
            float? currentSlopeDeg = 0f;
            if (!lastSample.Equals(double3.zero))
            {
                currentSlopeDeg = ProcessUnsmoothedNodes(nodesInBatch, container.Spline, lastSample, currentSample, lastSlopeDeg, false);
            }

            // Get the next few nodes to process.
            for (int j = 0; j < nodesPerSample && unsmoothedNodes.Count > 0; j++)
            {
                nodesInBatch.Add(unsmoothedNodes.Dequeue());
            }

            // If nodes could not be smoothed and there are still nodes left, skip to the next sample.
            // Do not update the last sample on smoothing failure in order to carry forward to last successful sample.
            if (!currentSlopeDeg.HasValue)
            {
                if (unsmoothedNodes.Count > 0) { continue; }
            }
            else
            {
                // Set the current sample to be the last sample for the next loop.
                lastSample = currentSample;
                lastSlopeDeg = currentSlopeDeg;
            }

            // If this is the final batch, try to sample the end node and process the final few nodes.
            if (unsmoothedNodes.Count == 0)
            {
                result = await tileset.SampleHeightMostDetailed(CoordinatesNodeToDouble3(nodesInBatch[^1]));
                if (result.sampleSuccess[0])
                {
                    currentSample = result.longitudeLatitudeHeightPositions[0];
                }
                ProcessUnsmoothedNodes(nodesInBatch, container.Spline, lastSample, currentSample, lastSlopeDeg, true);
            }
        }

        // Smoothen the spline.
        container.Spline.SetTangentMode(TangentMode.AutoSmooth);
    }

    private double3 CoordinatesNodeToDouble3(WayNode node)
    {
        return new double3(node.Lon, node.Lat, 1.0);
    }

    private float? ProcessUnsmoothedNodes(List<WayNode> nodes, Spline spline, double3 startSample, double3 endSample, float? lastSlopeDeg, bool endIncluded)
    {
        // Calculate the start and end positions.
        Vector3 startPos = ToUnityPosition(georeference, startSample[0], startSample[1], startSample[2]);
        Vector3 endPos = ToUnityPosition(georeference, endSample[0], endSample[1], endSample[2]);

        // Get the length of the way(s) in order to get the position of each node.
        List<Vector3> nodeWorldPositions = new()
            {
                ToUnityPosition(georeference, startSample[0], startSample[1])
            };
        List<double> cumulativeDistances = new();
        double totalDistance = 0;
        for (int j = 1; j < nodes.Count(); j++)
        {
            // Calculate the world position.
            Vector3 currentNodeWorldPos = ToUnityPosition(georeference, nodes[j].Lon, nodes[j].Lat);
            nodeWorldPositions.Add(currentNodeWorldPos);

            // Calculate the distance from the last point and add it to the total.
            float distance = Vector3.Distance(nodeWorldPositions[j - 1], currentNodeWorldPos);
            totalDistance += distance;
            cumulativeDistances.Add(totalDistance);
        }

        // Add the distance to the end position if not in the nodes to give a full range to linearly interpolate across.
        if (!endIncluded)
        {
            totalDistance += Vector3.Distance(nodeWorldPositions[^1], ToUnityPosition(georeference, endSample[0], endSample[1]));
        }

        // Calculate the slope to find anomalous points.
        // Any slopes above the specified limit are anomalous.
        var totalHeight = endPos.y - startPos.y;
        var slopeDeg = Mathf.Rad2Deg * Mathf.Asin((float)(totalHeight / totalDistance));
        if (Mathf.Abs(slopeDeg) > gradeDegreeLimit || lastSlopeDeg.HasValue && Mathf.Abs(lastSlopeDeg.Value - slopeDeg) > gradeDegreeChangeLimit)
        {
            Debug.Log($"Suspicious slope of {slopeDeg} degrees found between {startPos:F2} and {endPos:F2} (hypotenuse = {totalDistance}, opposite = {totalHeight})!");
            anomalousPositions.Add(transform.InverseTransformPoint(startPos));
            anomalousPositions.Add(transform.InverseTransformPoint(endPos));

            // Skip this sample if there are more nodes left to sample from.
            // Return null to indicate smoothing failure.
            if (!endIncluded) { return null; }
        }

        // Smoothen all unsmoothed nodes and add them to the spline.
        // Add the starting node to the spline.
        spline.Add(new BezierKnot(new float3(startPos.x, startPos.y, startPos.z)));
        for (int j = 1; j < nodes.Count(); j++)
        {
            // Use linear interpolation between the start and end sample heights to estimate the height of the node.
            float t = (float)(cumulativeDistances[j - 1] / totalDistance);
            float height = Mathf.Lerp(startPos.y, endPos.y, t);

            // Add the smoothed node to the spline.
            Vector3 pos = new(nodeWorldPositions[j].x, height, nodeWorldPositions[j].z);
            spline.Add(new BezierKnot(new float3(pos.x, pos.y, pos.z)));
        }

        // Clear the list after having processed all the previous ways.
        nodes.Clear();

        // Return the slope to indicate smoothing success.
        return slopeDeg;
    }

    private Vector3 ToUnityPosition(CesiumGeoreference georeference, double lon, double lat, double height = 0)
    {
        double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(lon, lat, height));
        double3 unityPos = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return new Vector3((float)unityPos.x, (float)unityPos.y, (float)unityPos.z);
    }

    float CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 6371; // Earth's radius in km
        var dLat = (lat2 - lat1) * Mathf.Deg2Rad;
        var dLon = (lon2 - lon1) * Mathf.Deg2Rad;
        var a = Mathf.Sin((float)dLat / 2) * Mathf.Sin((float)dLat / 2) +
                Mathf.Cos((float)lat1 * Mathf.Deg2Rad) * Mathf.Cos((float)lat2 * Mathf.Deg2Rad) *
                Mathf.Sin((float)dLon / 2) * Mathf.Sin((float)dLon / 2);
        var c = 2 * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1 - a));
        return (float)(R * c * 1000); // Convert to meters
    }

    void OnDrawGizmos()
    {
        // Draw spheres around any anomalous positions.
        Gizmos.color = Color.red;
        foreach (var localPos in anomalousPositions)
        {
            Vector3 worldPos = transform.TransformPoint(localPos);
            Gizmos.DrawWireSphere(worldPos, 3f);
        }
    }
}

/// <summary>
/// Defines the node of a way both via its position and whether it is a tunnel or not.
/// </summary>
public readonly struct WayNode
{
    public WayNode(double lat, double lon, bool tunnel = false)
    {
        Lat = lat;
        Lon = lon;
        IsTunnel = tunnel;
    }

    public double Lat { get; }
    public double Lon { get; }
    public bool IsTunnel { get; }

    public override string ToString() => $"{(IsTunnel ? "Tunnel " : "")}Node at ({Lat}, {Lon})";
}