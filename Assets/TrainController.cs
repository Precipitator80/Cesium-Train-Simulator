using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Controller to simulate a train moving along a spline following tracks.
/// Script adapted from SplineMoving by Yuchen_Chang.
/// Move First Person Character Controller Along a Spline - Yuchen_Chang - Accessed 21 May 2025
/// https://discussions.unity.com/t/move-first-person-character-controller-along-a-spline/918367
/// </summary>
public class TrainController : MonoBehaviour
{
    /// <summary>
    /// The spline container holding the spline to move along.
    /// </summary>
    [SerializeField] private SplineContainer splineContainer;

    /// <summary>
    /// The current position along the spline.
    /// </summary>
    private float splinePos = 0f;

    /// <summary>
    /// The current speed.
    /// </summary>
    private float speed = 0f;

    /// <summary>
    /// The acceleration of the train.
    /// </summary>
    private readonly float acceleration = 0.611111111111f;

    /// <summary>
    /// The deceleration of the train.
    /// </summary>
    private readonly float deceleration = 1.13888888889f;

    private void Update()
    {
        UpdateSpeed();
        UpdatePosOnSpline();
    }

    /// <summary>
    /// Updates the speed of the train.
    /// </summary>
    private void UpdateSpeed()
    {
        // Apply the engine when pressing W and apply the brakes when pressing S.
        if (Input.GetKey(KeyCode.W))
        {
            speed += acceleration * Time.deltaTime;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            speed = Mathf.Max(0f, speed - deceleration * Time.deltaTime);
        }

        // Show the kilometres per hour to one decimal place.
        float kmh = Mathf.Round(speed * 36f) / 10;
        Debug.Log("Current speed: " + kmh + " km/h");
    }


    /// <summary>
    /// Updates the position of the train on the spline.
    /// </summary>
    private void UpdatePosOnSpline()
    {
        // Calculate the new position on the spline.
        var splineLength = splineContainer.Spline.GetLength();
        splinePos = Mathf.Clamp(splinePos + speed * Time.deltaTime, 0f, splineLength);

        // Normalise the new spline position along the spline's length.
        var normalizedPos = splinePos / splineLength;

        // Use the normalised position to get the position on the spline relative to the spline container.
        Vector3 posFromContainer = splineContainer.Spline.EvaluatePosition(normalizedPos);

        // Transform the position from spline container coordinates into world coordinates.
        Vector3 worldPos = splineContainer.transform.TransformPoint(posFromContainer + Vector3.up * 3f);

        // Set the train to the new position on the spline.
        transform.position = worldPos;
    }
}