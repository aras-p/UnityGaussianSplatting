using UnityEngine;
using System.Collections;

public class Rotater : MonoBehaviour {

	public float Speed;

	void Update () {
	
		this.transform.Rotate (0f, 1f * Speed, 0f, Space.Self);
	}
}
