using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CalmWater
{

	[RequireComponent(typeof(Projector))]
	public class CausticAnimator : MonoBehaviour 
	{
		[SerializeField]
		private float _frameDuration = 0.1f;
		[SerializeField]
		private Texture2D[] _causticFrames = null;
		private Projector _projector;
		private Material _mat;
		private WaitForSeconds _delay;
		private int _propID;
		private int _currentFrame = 0;

		void OnEnable()
		{
			if (_causticFrames.Length < 2) 
			{
				enabled = false;
			}

			if (_projector == null) 
			{
				_projector = GetComponent<Projector> ();
			}

			if (_mat == null) 
			{
				_mat = _projector.material;
			}

			_currentFrame = 0;
			_propID = Shader.PropertyToID ("_CausticTex");
			_delay 	= new WaitForSeconds (_frameDuration);
		}

		void OnDisabled()
		{
			StopCoroutine (AnimateCaustic ());
		}

		void Awake()
		{
			StartCoroutine (AnimateCaustic());
		}

		private int NextFrame()
		{
			_currentFrame ++;
			_currentFrame = _currentFrame >= _causticFrames.Length ? 0 : _currentFrame;

			return _currentFrame;
		}

		IEnumerator AnimateCaustic()
		{
			while (true) 
			{
				yield return _delay;
				_mat.SetTexture (_propID, _causticFrames[NextFrame ()]);
			}
		}

	}

}
