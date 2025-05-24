using UnityEngine;
using UnityEngine.Splines;
using TMPro;

/// <summary>
/// Controller to simulate a train moving along a spline following tracks.
/// Script adapted from SplineMoving by Yuchen_Chang.
/// Move First Person Character Controller Along a Spline - Yuchen_Chang - Accessed 21 May 2025
/// https://discussions.unity.com/t/move-first-person-character-controller-along-a-spline/918367
/// </summary>
public class TrainController : MonoBehaviour
{
    /// Unity components.

    /// <summary>
    /// The spline container holding the spline to move along.
    /// Expose publicly to let the OverpassQuerier set the spline after querying.
    /// </summary>
    public SplineContainer splineContainer;
    /// <summary>
    /// A text field to display train statistics.
    /// </summary>
    [SerializeField] private TextMeshProUGUI statsText;

    /// Variables changed across simulation.

    /// <summary>
    /// The current position along the spline.
    /// </summary>
    private float splinePos = 0f;
    /// <summary>
    /// The current speed.
    /// </summary>
    private float speed = 0f;

    /// Physical constants of the specific train being modelled.

    /// <summary>
    /// The mass of the entire train.
    /// </summary>
    public int mass = 104500;
    /// <summary>
    /// The number of bogies across the entire train.
    /// </summary>
    public int totalBogieCount = 6;
    /// <summary>
    /// The number of driven bogies across the entire train.
    /// </summary>
    public int drivenBogieCount = 4;
    /// <summary>
    /// The number of motors on each driven bogie.
    /// </summary>
    public int motorsPerBogie = 2;
    /// <summary>
    /// The power per motor.
    /// </summary>
    public int motorPower = 230000;
    /// <summary>
    /// The efficiency of the motors.
    /// </summary>
    public float motorEfficiency = 0.82f;
    /// <summary>
    /// Coefficient that affects the strength of drag. Represents how streamlined the train is.
    /// </summary>
    public float coefficientOfDrag = 1.3f;
    /// <summary>
    /// The cross-sectional area of the train. Represents how much surface is subject to drag.
    /// </summary>
    public float crossSectionalArea = 13f;
    /// <summary>
    /// The braking deceleration of the train.
    /// </summary>
    public float brakingDeceleration = 1.13888f;

    /// Physical constants not dependent on the train model in this model.

    /// <summary>
    /// Coefficient of friction for steel on steel not moving relative to each other. Can vary by track conditions.
    /// Also called the adhesion coefficient.
    /// </summary>
    public float coefficientOfStaticFriction = 0.097f;
    /// <summary>
    /// Coefficient of friction for steel sliding against steel. Can vary by track conditions.
    /// Represents imperfections in the wheel and railway. Does NOT represent friction when the train is moving. Analogous to a locked wheel sliding along the track.
    /// Also called rolling friction and kinetic friction.
    /// </summary>
    public float coefficientOfSlidingFriction = 0.0015f;
    /// <summary>
    /// The density of the air. Influences drag.
    /// </summary>
    public float airDensity = 1.225f; // Assuming 15 degrees Celsius.

    /// <summary>
    /// Calculates the acceleration to update the speed and then uses the speed to update the position.
    /// </summary>
    private void Update()
    {
        // Calculate physical properties used in further calculation.
        float weight = mass * Mathf.Abs(Physics.gravity.y);
        float drivenWeight = weight * ((float)drivenBogieCount / totalBogieCount);
        float tractiveEffortMax = coefficientOfStaticFriction * drivenWeight;

        // Apply motors when pressing W.
        float tractiveEffort = 0f;
        if (Input.GetKey(KeyCode.W))
        {
            if (speed > 0f)
            {
                float power = motorsPerBogie * drivenBogieCount * motorPower * motorEfficiency;
                tractiveEffort = Mathf.Min(power / speed, tractiveEffortMax); // Ensure the tractive effort used is not above the maximum.
            }
            else
            {
                tractiveEffort = tractiveEffortMax; // Use maximum tractive effort at zero speed to avoid division by zero.
            }

        }

        float slopeAngle = 0f; // Assume flat grade for now. This should be calculated dynamically later (TODO).

        // Calculate opposing forces.
        float fFriction = coefficientOfSlidingFriction * weight * Mathf.Cos(slopeAngle);
        float fAirDrag = 0.5f * airDensity * coefficientOfDrag * crossSectionalArea * Mathf.Pow(speed, 2);
        float fGrade = weight * Mathf.Sin(slopeAngle);
        float fBrake = Input.GetKey(KeyCode.S) ? brakingDeceleration * mass : 0; // Apply brakes when pressing S.

        // Sum all the forces to calculate the acceleration.
        float acceleration = (tractiveEffort - fFriction - fAirDrag - fGrade - fBrake) / mass;

        // Update the speed with the acceleration, ensuring the speed does not drop below 0.
        speed = Mathf.Max(0f, speed + acceleration * Time.deltaTime);

        // Update the position of the train.
        UpdatePosOnSpline();

        // Show the kilometres per hour to one decimal place.
        float speedKMH = speed * 3.6f;
        float accelerationKMHS = acceleration * 3.6f;
        statsText.text = $"Speed: {speedKMH:F2} km/h\n" +
                     $"Speed {speed:F2} m/s\n" +
                     $"Acceleration: {accelerationKMHS:F2} km/h/s\n" +
                     $"Acceleration: {acceleration:F2} m/s²\n" +
                     $"Spline Pos: {splinePos:F2} m\n" +
                     $"Weight: {weight:F2} N\n" +
                     $"Driven Weight: {drivenWeight:F2} N\n" +
                     $"Tractive effort: {tractiveEffort:F2} N (max {tractiveEffortMax:F2} N)\n" +
                     $"Friction: {fFriction:F2} N\n" +
                     $"Drag: {fAirDrag:F2} N\n" +
                     $"Grade Friction: {fGrade:F2} N\n" +
                     $"Brake Force: {fBrake:F2} N\n";
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