using UnityEngine;
using UnityEditor;

/// <summary>
/// Adds buttons to the editor to spawn and clear objects.
/// </summary>
[CustomEditor(typeof(OverpassQuerier))]
public class OverpassQuerierEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        OverpassQuerier querier = (OverpassQuerier)target;

        if (GUILayout.Button("Trigger Query"))
        {
            querier.TriggerQuery();
        }

        if (GUILayout.Button("Clear Objects"))
        {
            querier.ClearObjects();
        }
    }
}
