using UnityEngine;
using System.Collections;
#if UNITY_5_0 && !UNITY_5_1 || !UNITY_5_2
using UnityEngine.SceneManagement;
#endif

namespace CalmWater{
	public class SceneSwitcher : MonoBehaviour {

		public void SwitchLevel(string level){
			#if UNITY_5_0 && !UNITY_5_1 || !UNITY_5_2
			SceneManager.LoadScene (level);	
			#else
			Application.LoadLevel(level);
			#endif
		}
	}
}

