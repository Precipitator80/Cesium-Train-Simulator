using UnityEngine;
using UnityEngine.Networking;
using Unity.Mathematics;
using System.Collections;
using CesiumForUnity;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine.Splines;

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

    public List<long> stationIdsOrdered = new();
    public List<long> trackIdsOrdered = new();
    public Dictionary<long, JToken> stations = new();
    public Dictionary<long, JToken> tracks = new();

    /// <summary>
    /// Clears any objects and then queries Overpass Turbo, spawning a new set of objects.
    /// </summary>
    public void TriggerQuery()
    {
        ClearObjects();
        StartCoroutine(QueryOverpass());
    }

    /// <summary>
    /// Clears all currently spawned objects.
    /// </summary>
    public void ClearObjects()
    {
        StopAllCoroutines();
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

    IEnumerator QueryOverpass()
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
                ProcessJson(webRequest.downloadHandler.text);
            }
            else
            {
                Debug.LogError("Error: " + webRequest.error);
            }
        }
    }

    void ProcessJson(string jsonString)
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
        StartCoroutine(GenerateSplineWithTerrainHeights());
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

    IEnumerator GenerateSplineWithTerrainHeights()
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

        // Flag to update the origin to be at the spline.
        bool setOrigin = false;

        // Add all of the tracks' nodes to the spline.
        foreach (var id in trackIdsOrdered)
        {
            foreach (var coordinates in tracks[id]["geometry"])
            {
                // Sample height from the terrain.
                double lat = (double)coordinates["lat"];
                double lon = (double)coordinates["lon"];
                Task<CesiumSampleHeightResult> task = tileset.SampleHeightMostDetailed(new double3(lon, lat, 1.0));
                yield return new WaitForTask(task);
                CesiumSampleHeightResult result = task.Result;
                if (result.sampleSuccess[0])
                {
                    // Get the sampled position.
                    double3 sampledPos = result.longitudeLatitudeHeightPositions[0];

                    // Check whether the origin has been set yet.
                    if (!setOrigin)
                    {
                        // Set the origin to just above the first part of the track and reset the position of the querier (usually attached to the camera).
                        georeference.SetOriginLongitudeLatitudeHeight(sampledPos[0], sampledPos[1], sampledPos[2] + 3f);
                        transform.position = Vector3.zero;

                        // Add a globe anchor and set the georeference as the parent to keep the container's position accurate to the world.
                        splineGameObject.AddComponent<CesiumGlobeAnchor>();
                        splineGameObject.transform.SetParent(georeference.transform);

                        // Update the flag to avoid updating the origin again.
                        setOrigin = true;
                    }

                    // Add a knot at the sampled position in Unity space.
                    Vector3 pos = ToUnityPosition(georeference, sampledPos[0], sampledPos[1], sampledPos[2]);
                    container.Spline.Add(new BezierKnot(new float3(pos.x, pos.y, pos.z)));
                }
            }
        }

        // Smoothen the spline.
        container.Spline.SetTangentMode(TangentMode.AutoSmooth);
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
}
