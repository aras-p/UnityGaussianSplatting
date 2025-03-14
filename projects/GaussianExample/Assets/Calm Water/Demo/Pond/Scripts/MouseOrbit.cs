using UnityEngine;
using System.Collections;

//[AddComponentMenu("Camera-Control/Mouse drag Orbit with zoom")]
public class MouseOrbit : MonoBehaviour
{
	public Transform target;
	public float distance = 5.0f;
	public float xSpeed = 120.0f;
	public float ySpeed = 120.0f;
	public float scrollSpeed = 1f;

	public float yMinLimit = -20f;
	public float yMaxLimit = 80f;

	public float distanceMin = .5f;
	public float distanceMax = 15f;

	public float smoothTime = 2f;

	float rotationYAxis = 0.0f;
	float rotationXAxis = 0.0f;

	float velocityX = 0.0f;
	float velocityY = 0.0f;

	private bool start = true;


	// Use this for initialization
	void Start()
	{
		Vector3 angles = transform.eulerAngles;
		rotationYAxis = angles.y;
		rotationXAxis = angles.x;

		// Make the rigid body not change rotation
//		if (rigidbody)
//		{
//			rigidbody.freezeRotation = true;
//		}
	}

	void LateUpdate()
	{
		if (target)
		{
			if (Input.GetMouseButton(1))
			{
				start = false;
				velocityX += xSpeed * Input.GetAxis("Mouse X") * 0.02f;
				velocityY += ySpeed * Input.GetAxis("Mouse Y") * 0.02f;
			}

			if (start) {
				return;
			}

			rotationYAxis += velocityX;
			rotationXAxis -= velocityY;

			rotationXAxis = ClampAngle(rotationXAxis, yMinLimit, yMaxLimit);

			//Quaternion fromRotation = Quaternion.Euler(transform.rotation.eulerAngles.x, transform.rotation.eulerAngles.y, 0);
			Quaternion toRotation 	= Quaternion.Euler (rotationXAxis, rotationYAxis, 0);
			Quaternion rotation 	= toRotation;

			Vector3 negDistance = new Vector3(0.0f, 0.0f, -distance);
			Vector3 position = rotation * negDistance + target.position;

			transform.rotation = rotation;
			transform.position = position;

			velocityX = Mathf.Lerp(velocityX, 0, Time.deltaTime * smoothTime);
			velocityY = Mathf.Lerp(velocityY, 0, Time.deltaTime * smoothTime);


			float wheel = Input.GetAxis("Mouse ScrollWheel");
			if (wheel < 0f && distance < distanceMax)
			{
				// scroll down
				distance += scrollSpeed;
			}
			else if (wheel > 0f && distance > distanceMin)
			{
				// scroll up
				distance -= scrollSpeed;
			}
		}

	}

	public static float ClampAngle(float angle, float min, float max)
	{
		if (angle < -360F)
			angle += 360F;
		if (angle > 360F)
			angle -= 360F;
		return Mathf.Clamp(angle, min, max);
	}
}