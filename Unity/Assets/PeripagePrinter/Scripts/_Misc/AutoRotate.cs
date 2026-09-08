using UnityEngine;

/// <summary>
/// Continuously rotates this GameObject around a chosen axis at a fixed speed.
/// Attach directly to any object you want to spin.
/// </summary>
public class AutoRotate : MonoBehaviour
{
    [Tooltip("Degrees per second.")]
    public float speed = 30f;

    [Tooltip("Axis to rotate around, in local space.")]
    public Vector3 axis = Vector3.up;

    void Update()
    {
        transform.Rotate(axis, speed * Time.deltaTime, Space.Self);
    }
}
