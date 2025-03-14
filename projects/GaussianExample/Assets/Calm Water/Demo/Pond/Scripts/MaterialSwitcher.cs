using UnityEngine;
using System.Collections;

namespace CalmWater {
	public class MaterialSwitcher : MonoBehaviour {

		public MeshRenderer WaterPlane;
		public Material ClassicMat;
		public Material DX11Mat;

		private MirrorReflection m;

		void Start(){
			m = WaterPlane.GetComponent<MirrorReflection> ();	
		}

		public void SetDX11Mat(){
			WaterPlane.material = DX11Mat;
			m.setMaterial ();
		}

		public void SetClassicMat(){
			WaterPlane.material = ClassicMat;
			m.setMaterial ();
		}
	}
}
