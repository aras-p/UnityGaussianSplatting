using UnityEngine;
using System.Collections;

[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(UnderWaterFog))]
[ExecuteInEditMode]
public class FogControl : MonoBehaviour {

	public float FadeSpeed = 10f;
	private float Rate = 1f;

	private UnderWaterFog fog;
	private Camera cam;	

	void OnEnable(){
		init ();
	}

	void Start(){
		init ();
	}

	void Update () {

		Rate += Time.deltaTime / FadeSpeed;
		Rate = Mathf.Clamp(Rate, 0, FadeSpeed);

		//Under Water
		if (cam.transform.position.y <= fog.height) {
			if (!fog.enabled) {
				fog.enabled = true;
			}
			fog.fogColor.a = Mathf.Lerp(fog.fogColor.a, 1f, Rate);

		} else {
		//Over water
			fog.fogColor.a = Mathf.Lerp(fog.fogColor.a, 0f, Rate * 2f);
			if (fog.fogColor.a <= 0.01f) {
				fog.enabled = false;
			}
		}
	}

	private void init(){
		if (cam == null) {
			cam = GetComponent<Camera> ();
		}

		if (fog == null) {
			fog = GetComponent<UnderWaterFog> ();
		}

		if (cam.transform.position.y >= fog.height) {
			fog.fogColor.a = 0f;
		}
	}
}
