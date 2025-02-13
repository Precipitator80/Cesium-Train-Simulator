using UnityEngine;
using UnityEngine.Networking;
using Unity.Mathematics;
using System.Collections;
using CesiumForUnity;
using Newtonsoft.Json.Linq;

public class OverpassQuerier : MonoBehaviour
{
    public GameObject cuboidPrefab;
    private CesiumGlobeAnchor anchor;
    private CesiumGeoreference georeference;
    private string json;

    void Start()
    {
        anchor = GetComponent<CesiumGlobeAnchor>();
        georeference = GetComponentInParent<CesiumGeoreference>();
        StartCoroutine(QueryOverpass());
    }

    IEnumerator QueryOverpass()
    {
        string query = $@"[out:json];
        (
          node[""railway""=""station""][""station""!~""subway""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
          node[""railway""=""station""][""station""=""subway""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
          way[""railway""=""rail""](around:100,{anchor.longitudeLatitudeHeight.y},{anchor.longitudeLatitudeHeight.x});
        );
        out geom;";

        string encodedQuery = UnityWebRequest.EscapeURL(query);
        string url = "https://overpass-api.de/api/interpreter?data=" + encodedQuery;

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                json = webRequest.downloadHandler.text;
                ProcessJson(json);
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

        foreach (var element in elements)
        {
            string type = (string)element["type"];
            JObject tags = (JObject)element["tags"];

            if (type == "node")
            {
                double lat = (double)element["lat"];
                double lon = (double)element["lon"];
                SpawnObject(lat, lon, tags);
            }
            else if (type == "way")
            {
                JArray geometry = (JArray)element["geometry"];
                SpawnWay(geometry, tags);
            }
        }
    }

    void SpawnObject(double lat, double lon, JObject tags)
    {
        GameObject cuboid = Instantiate(cuboidPrefab, georeference.transform);
        cuboid.SetActive(true);
        CesiumGlobeAnchor cuboidAnchor = cuboid.AddComponent<CesiumGlobeAnchor>();
        cuboidAnchor.longitudeLatitudeHeight = new double3(lon, lat, 80);

        // Adjust scale for visibility
        cuboid.transform.localScale = new Vector3(5, 5, 5);

        Debug.Log($"Spawned object at {lat}, {lon} with tags: {tags}");
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

            SpawnCuboidBetweenPoints(lat1, lon1, lat2, lon2);
        }

        Debug.Log($"Spawned way with {geometry.Count} points and tags: {tags}");
    }

    void SpawnCuboidBetweenPoints(double lat1, double lon1, double lat2, double lon2)
    {
        GameObject cuboid = Instantiate(cuboidPrefab, georeference.transform);
        cuboid.SetActive(true);
        CesiumGlobeAnchor cuboidAnchor = cuboid.AddComponent<CesiumGlobeAnchor>();



        // Set position to midpoint of the two nodes
        cuboidAnchor.longitudeLatitudeHeight = new double3(
            (lon1 + lon2) / 2,
            (lat1 + lat2) / 2,
            80  // Assuming ground level, adjust if needed
        );

        // Calculate rotation to align with track direction
        float angle = Mathf.Atan2((float)(lon2 - lon1), (float)(lat2 - lat1)) * Mathf.Rad2Deg;
        cuboid.transform.localRotation = Quaternion.Euler(0, angle, 0);

        // Scale cuboid to fit between points (rough approximation)
        float distance = CalculateDistance(lat1, lon1, lat2, lon2);
        cuboid.transform.localScale = new Vector3(2, 2, distance);
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
