using System;
//using System.Reflection;
using UnityEngine;

namespace UnityEditor
{
	public class CalmWaterInspector: ShaderGUI
	{
		//Color
		MaterialProperty shallowColor 	= null;
		MaterialProperty depthColor 	= null;
		MaterialProperty depthStart		= null;
		MaterialProperty depthEnd		= null;
		MaterialProperty enableDepthFog	= null;

        //Scatter
        MaterialProperty enableScatter  = null;
        MaterialProperty scatterColor   = null;
        MaterialProperty scatterParams  = null;

		MaterialProperty edgeFade 		= null;

		//Spec
		MaterialProperty specColor      = null;
		MaterialProperty smoothness     = null;
        MaterialProperty specFresnel    = null;
        MaterialProperty specIntensity = null;

        //NormalMap
        MaterialProperty bumpMode           = null;
		MaterialProperty bumpMap 	        = null;
		MaterialProperty largeBump 	        = null;
		MaterialProperty bumpStrength       = null;
		MaterialProperty bumpLargeStrength  = null;
		MaterialProperty worldSpace         = null;

        //FlowMap
        MaterialProperty flowMap            = null;
        MaterialProperty flowSpeed          = null;
        MaterialProperty flowIntensity      = null;

		//Animation
		MaterialProperty speeds = null;
		MaterialProperty speedsLarge = null;
		//Distortion
		MaterialProperty distortion = null;
		MaterialProperty distortionQuality = null;
				
		//Reflection
		MaterialProperty reflectionType = null;
		MaterialProperty cubeColor = null;
		MaterialProperty cubeMap = null;

		//MaterialProperty reflectionTex = null;
		MaterialProperty cubeDist	= null;
		MaterialProperty reflection = null;
		MaterialProperty fresnel 	= null;
		MaterialProperty reflectionNormals = null;
				
		//Foam
		MaterialProperty foamToggle = null;
		MaterialProperty foamColor = null;
		MaterialProperty foamTex = null;
		MaterialProperty foamSize = null;
		MaterialProperty whiteCaps = null;
		MaterialProperty capsIntensity = null;
		MaterialProperty capsMask = null;
		MaterialProperty capsSpeed = null;
		MaterialProperty capsSize = null;

        // Caustics
        MaterialProperty caustics = null;
        MaterialProperty causticsTex = null;
        MaterialProperty causticsIntensity = null;
        MaterialProperty causticsStart = null;
        MaterialProperty causticsEnd = null;
        MaterialProperty causticsSpeed = null;

        //Displacement
        MaterialProperty displacementMode = null;
		MaterialProperty amplitude 	= null;
		MaterialProperty frequency 	= null;
		MaterialProperty speed		= null;

		MaterialProperty steepness 			    = null;
		MaterialProperty waveSpeed 			    = null;
		MaterialProperty waveDirectionXY 	    = null;
		MaterialProperty waveDirectionZW 	    = null;
        MaterialProperty displacementTexture    = null;
        MaterialProperty displacementSpeed      = null;


		MaterialProperty smoothing 			= null;

		MaterialProperty tess 				= null;
		private bool _hasTess = false;

		MaterialEditor m_MaterialEditor;


		private bool _ShowColor = true;
		private bool _ShowScatter = true;
		private bool _ShowSpecular = true;
		private bool _ShowBump = true;
		private bool _ShowReflection = true;
		private bool _ShowFoam = true;
		private bool _ShowCaustics = true;
		private bool _ShowDisplacement = true;
		private bool _ShowTessellation = true;
		private bool _ShowOptions = true;


		GUIStyle _FoldoutStyle;


		private const string _cVersion = "1.10.0";
		
		public void FindProperties(MaterialProperty[] props)
		{

			//Color
			shallowColor 	= FindProperty ("_Color", props);
			depthColor 		= FindProperty ("_DepthColor", props);
			depthStart		= FindProperty ("_DepthStart", props);
			depthEnd 		= FindProperty ("_DepthEnd", props);
			enableDepthFog	= FindProperty ("_EnableFog",props);
			edgeFade		= FindProperty ("_EdgeFade",props);

            //Scatter
            enableScatter   = FindProperty("_Scatter", props);
            scatterColor    = FindProperty("_ScatterColor", props);
            scatterParams   = FindProperty("_ScatterParams", props);


            //Spec
            specColor 		= FindProperty ("_SpecColor", props);
			smoothness 		= FindProperty ("_Smoothness", props);

            specFresnel     = FindProperty("_specFresnel", props);
            specIntensity   = FindProperty("_specIntensity", props);

            //NormalMap
            bumpMode            = FindProperty("_BumpMode", props);
			bumpMap 		    = FindProperty ("_BumpMap", props);
			largeBump 		    = FindProperty ("_BumpMapLarge", props);
			bumpStrength 	    = FindProperty ("_BumpStrength", props);
			bumpLargeStrength   = FindProperty ("_BumpLargeStrength", props);
            //FlowMap
            flowMap         = FindProperty("_FlowMap", props);
            flowSpeed       = FindProperty("_FlowSpeed", props);
            flowIntensity   = FindProperty("_FlowIntensity", props);

			//Animation
			worldSpace		= FindProperty ("_WorldSpace",props);
			speeds 			= FindProperty ("_Speeds", props);
			speedsLarge 	= FindProperty ("_SpeedsLarge", props);
			
			//Distortion
			distortion 			= FindProperty ("_Distortion", props);
			distortionQuality 	= FindProperty ("_DistortionQuality", props);
			
			//Reflection
			reflectionType 	= FindProperty ("_ReflectionType", props);
			cubeColor 		= FindProperty ("_CubeColor", props);
			cubeMap 		= FindProperty ("_Cube", props);
			cubeDist 		= FindProperty ("_CubeDist", props);
			reflection 		= FindProperty ("_Reflection", props);
			fresnel 		= FindProperty ("_RimPower", props);
			reflectionNormals = FindProperty("_ReflectionNormals", props);

			//Foam
			foamToggle 		= FindProperty ("_FOAM", props);
			foamColor 		= FindProperty ("_FoamColor", props);
			foamTex			= FindProperty ("_FoamTex", props);
			foamSize 		= FindProperty ("_FoamSize", props);
			whiteCaps 		= FindProperty ("_WhiteCaps", props);
			capsIntensity 	= FindProperty ("_CapsIntensity", props);
			capsMask 		= FindProperty ("_CapsMask", props);
			capsSpeed 		= FindProperty ("_CapsSpeed", props);
			capsSize		= FindProperty ("_CapsSize", props);

            // Caustics
            caustics            = FindProperty("_Caustics", props);
            causticsTex         = FindProperty("_CausticsTex", props);
            causticsIntensity   = FindProperty("_CausticsIntensity", props);
            causticsStart       = FindProperty("_CausticsStart", props);
            causticsEnd         = FindProperty("_CausticsEnd", props);
            causticsSpeed       = FindProperty("_CausticsSpeed", props);

			//Displacement
			displacementMode 	= FindProperty ("_DisplacementMode", props);
			amplitude 			= FindProperty ("_Amplitude", props);
			frequency 			= FindProperty ("_Frequency", props);
			speed				= FindProperty ("_Speed",props);
			waveSpeed 			= FindProperty ("_WSpeed", props);
			steepness 			= FindProperty ("_Steepness", props);
			waveDirectionXY 	= FindProperty ("_WDirectionAB", props);
			waveDirectionZW 	= FindProperty ("_WDirectionCD", props);
            displacementTexture = FindProperty("_DisplacementTex", props);
            displacementSpeed   = FindProperty("_DisplacementSpeed", props);

            smoothing 			= FindProperty ("_Smoothing", props);
			if(_hasTess){
				tess 				= FindProperty ("_Tess", props);
			}

		}
		
		public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] props)
		{

			_FoldoutStyle = new GUIStyle(EditorStyles.foldout);
			_FoldoutStyle.fontStyle = FontStyle.Bold;

			m_MaterialEditor = materialEditor;
			Material material = materialEditor.target as Material;

			if(material.HasProperty("_Tess")){
				_hasTess = true;
			}else{
				_hasTess = false;
			}

            Header();
            FindProperties(props); // MaterialProperties can be animated so we do not cache them but fetch them every event to ensure animated values are updated correctly
			ShaderPropertiesGUI(material);
		}

        public void Header()
        {
            // Use default labelWidth
            EditorGUIUtility.labelWidth = 0f;
            EditorGUIUtility.fieldWidth = 64f;

            Texture2D tex = Resources.Load("CalmWaterLogo") as Texture2D;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(tex);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        public void ShaderPropertiesGUI(Material material)
		{
			// Detect any changes to the material
			EditorGUI.BeginChangeCheck();
			{
				DrawColor();
				DrawScatter();
				DrawSpecular();
				DrawBump();
				DrawReflection();
				DrawFoam();
				DrawCaustics();
				DrawDisplacement();
				DrawTessellation();
				DrawOptions();

                // Queue
                material.renderQueue = EditorGUILayout.IntField("Render Queue", material.renderQueue);

				// Version
				GUIStyle boldRight = new GUIStyle ();
				boldRight.alignment = TextAnchor.MiddleRight;
				boldRight.fontStyle = FontStyle.Bold;

				GUILayout.Label ("Version " + _cVersion,boldRight);
				GUILayout.Space (3f);
            }
        }

		void DrawColor()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowColor = EditorGUILayout.Foldout(_ShowColor, "Color", _FoldoutStyle);

			if (_ShowColor)
			{
				m_MaterialEditor.ShaderProperty(shallowColor, "Shallow Color");
				m_MaterialEditor.ShaderProperty(depthColor, "Depth Color");
				m_MaterialEditor.ShaderProperty(depthStart, "Depth Start");
				m_MaterialEditor.ShaderProperty(depthEnd, "Depth End");
				m_MaterialEditor.ShaderProperty(enableDepthFog, "Enable Depth Fog");
				m_MaterialEditor.ShaderProperty(edgeFade, "Edge Fade");
			}
			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawScatter()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowScatter = EditorGUILayout.Foldout(_ShowScatter, "Scattering", _FoldoutStyle);

			if (_ShowScatter)
			{
				m_MaterialEditor.ShaderProperty(enableScatter, "Enable");
			}
			

			if (enableScatter.floatValue == 1f && _ShowScatter)
			{
				m_MaterialEditor.ShaderProperty(scatterColor, "Color");

				Vector4 scatterValues = scatterParams.vectorValue;
				float scatterIntensity = EditorGUILayout.FloatField("Intensity", scatterValues.x);
				float scatterHeight = 0f;

				if (displacementMode.floatValue != 0f)
				{
					scatterHeight = EditorGUILayout.FloatField("Height", scatterValues.y);
				}

				float scatterRange = EditorGUILayout.FloatField("Range", scatterValues.z);

				scatterParams.vectorValue = new Vector4(scatterIntensity, scatterHeight, scatterRange, 1f);
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}
		
		void DrawSpecular()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowSpecular = EditorGUILayout.Foldout(_ShowSpecular, "Specular", _FoldoutStyle);

			if (_ShowSpecular)
			{
				m_MaterialEditor.ShaderProperty(specColor, "Specular Color");
				m_MaterialEditor.ShaderProperty(smoothness, "Smoothness");

				GUILayout.Label("Fresnel Specular", EditorStyles.boldLabel);
				m_MaterialEditor.ShaderProperty(specIntensity, "Intensity");
				m_MaterialEditor.ShaderProperty(specFresnel, "Fresnel");
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawBump()
        {

			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowBump = EditorGUILayout.Foldout(_ShowBump, "Normal Maps", _FoldoutStyle);

			if (_ShowBump)
			{
				m_MaterialEditor.ShaderProperty(bumpMode, "NormalMap Mode");

				if (bumpMode.floatValue == 0 || bumpMode.floatValue == 1)
				{
					m_MaterialEditor.ShaderProperty(bumpMap, "Micro Bump");
					m_MaterialEditor.ShaderProperty(bumpStrength, "Bump Strength");

					if (bumpMode.floatValue == 1)
					{
						m_MaterialEditor.ShaderProperty(largeBump, "Large Bump");
						m_MaterialEditor.ShaderProperty(bumpLargeStrength, "Bump Strength");
					}

					GUILayout.Label("Scroll Animation", EditorStyles.boldLabel);

					// Animation speeds
					Vector2 speeds1 = EditorGUILayout.Vector2Field("Micro Speed 1", new Vector2(speeds.vectorValue.x, speeds.vectorValue.y));
					Vector2 speeds2 = EditorGUILayout.Vector2Field("Micro Speed 2", new Vector2(speeds.vectorValue.z, speeds.vectorValue.w));

					speeds.vectorValue = new Vector4(speeds1.x, speeds1.y, speeds2.x, speeds2.y);

					if (bumpMode.floatValue == 1)
					{
						GUILayout.BeginHorizontal();
						Vector4 LargeSpeed = speedsLarge.vectorValue;

						Vector2 GUILargeSpeed = EditorGUILayout.Vector2Field("Large Speed", new Vector2(LargeSpeed.x, LargeSpeed.y));

						speedsLarge.vectorValue = new Vector4(GUILargeSpeed.x, GUILargeSpeed.y, LargeSpeed.z, LargeSpeed.w);

						GUILayout.EndHorizontal();
					}
				}
				//FlowMaps
				if (bumpMode.floatValue == 2)
				{
					m_MaterialEditor.ShaderProperty(bumpMap, "Normal Map");
					m_MaterialEditor.ShaderProperty(bumpStrength, "Normal Strength");
					m_MaterialEditor.ShaderProperty(flowMap, "FlowMap");
					m_MaterialEditor.ShaderProperty(flowSpeed, "Flow Speed");
					m_MaterialEditor.ShaderProperty(flowIntensity, "Flow Intensity");

				}

				//Distortion
				GUILayout.Label("Distortion", EditorStyles.boldLabel);
				m_MaterialEditor.ShaderProperty(distortion, "Distortion");
				m_MaterialEditor.ShaderProperty(distortionQuality, "Distortion Quality");
				
			}

			EditorGUI.indentLevel = 1;
			GUILayout.EndVertical();
		}

		void DrawReflection()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowReflection = EditorGUILayout.Foldout(_ShowReflection, "Reflections", _FoldoutStyle);


			if (_ShowReflection)
			{
				m_MaterialEditor.ShaderProperty(reflectionType, "Reflection Type");

				switch ((int)reflectionType.floatValue)
				{
					// Mixed Mode Reflections
					case 1:
						EditorGUILayout.HelpBox("You need to add MirrorReflection script to your object.", MessageType.Info);
						m_MaterialEditor.ShaderProperty(cubeColor, "Cube Color");
						m_MaterialEditor.ShaderProperty(cubeMap, "Cube Map");
						m_MaterialEditor.ShaderProperty(cubeDist, "Cube Map Distortion");
						m_MaterialEditor.ShaderProperty(reflection, "Reflection");
						m_MaterialEditor.ShaderProperty(fresnel, "Fresnel");
						m_MaterialEditor.ShaderProperty(reflectionNormals, "Normals");
						break;
					// RealTime Mode Reflections
					case 2:
						EditorGUILayout.HelpBox("You need to add MirrorReflection script to your object.", MessageType.Info);
						m_MaterialEditor.ShaderProperty(reflection, "Reflection");
						m_MaterialEditor.ShaderProperty(fresnel, "Fresnel");
						m_MaterialEditor.ShaderProperty(reflectionNormals, "Normals");
						break;
					// CubeMap Reflections
					case 3:
						m_MaterialEditor.ShaderProperty(cubeColor, "Cube Color");
						m_MaterialEditor.ShaderProperty(cubeMap, "Cube Map");
						m_MaterialEditor.ShaderProperty(cubeDist, "Cube Map Distortion");
						m_MaterialEditor.ShaderProperty(reflection, "Reflection");
						m_MaterialEditor.ShaderProperty(fresnel, "Fresnel");
						m_MaterialEditor.ShaderProperty(reflectionNormals, "Normals");
						break;
					case 4:
						break;
				}
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawFoam()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowFoam = EditorGUILayout.Foldout(_ShowFoam, "Foam", _FoldoutStyle);

			if (_ShowFoam)
			{
				m_MaterialEditor.ShaderProperty(foamToggle, "Enable Foam");

				if (foamToggle.floatValue == 1)
				{
					m_MaterialEditor.ShaderProperty(foamColor, "Foam Color");
					m_MaterialEditor.ShaderProperty(foamTex, "Foam Texture");
					m_MaterialEditor.ShaderProperty(foamSize, "Foam Size");
				}

				// White Caps
				m_MaterialEditor.ShaderProperty(whiteCaps, "Enable White Caps");

				if (whiteCaps.floatValue == 1)
				{
					m_MaterialEditor.ShaderProperty(capsIntensity, "Caps Intensity");
					m_MaterialEditor.ShaderProperty(capsMask, "Caps Mask");
					m_MaterialEditor.ShaderProperty(capsSpeed, "Caps Speed");
					m_MaterialEditor.ShaderProperty(capsSize, "Caps Smooth");
				}
			}
			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawCaustics()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowCaustics = EditorGUILayout.Foldout(_ShowCaustics, "Caustics", _FoldoutStyle);

			if (_ShowCaustics)
			{
				m_MaterialEditor.ShaderProperty(caustics, "Enable Caustics");
				if (caustics.floatValue == 1)
				{
					m_MaterialEditor.ShaderProperty(causticsTex, "Texture");
					m_MaterialEditor.ShaderProperty(causticsIntensity, "Intensity");
					m_MaterialEditor.ShaderProperty(causticsStart, "Depth Start");
					m_MaterialEditor.ShaderProperty(causticsEnd, "Depth End");
					m_MaterialEditor.ShaderProperty(causticsSpeed, "Speed");
				}
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawDisplacement()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowDisplacement = EditorGUILayout.Foldout(_ShowDisplacement, "Displacement", _FoldoutStyle);


			if (_ShowDisplacement)
			{
				m_MaterialEditor.ShaderProperty(displacementMode, "Mode");

				if (displacementMode.floatValue != 0f)
				{

					EditorGUILayout.HelpBox("You need enough subdivisions in your Geometry.", MessageType.Info);
					EditorGUILayout.HelpBox("To get correct displaced normals, your model needs to be scaled [1,1,1].", MessageType.Info);
				}

				if (displacementMode.floatValue == 1f)
				{
					m_MaterialEditor.ShaderProperty(amplitude, "Amplitude");
					m_MaterialEditor.ShaderProperty(frequency, "Frequency");
					m_MaterialEditor.ShaderProperty(speed, "Waves Speed");
					m_MaterialEditor.ShaderProperty(smoothing, "Smoothing");
				}

				if (displacementMode.floatValue == 2f)
				{

					m_MaterialEditor.ShaderProperty(amplitude, "Amplitude");
					m_MaterialEditor.ShaderProperty(frequency, "Frequency");
					m_MaterialEditor.ShaderProperty(steepness, "Steepness");
					m_MaterialEditor.ShaderProperty(waveSpeed, "Waves Speed");
					m_MaterialEditor.ShaderProperty(waveDirectionXY, "Waves Directions 1");
					m_MaterialEditor.ShaderProperty(waveDirectionZW, "Waves Directions 2");

					m_MaterialEditor.ShaderProperty(smoothing, "Smoothing");
				}

				if (displacementMode.floatValue == 3f)
				{
					m_MaterialEditor.ShaderProperty(displacementTexture, "Displacement Texture");
					m_MaterialEditor.ShaderProperty(amplitude, "Amplitude");


					GUILayout.Label("Wave Speeds", EditorStyles.boldLabel);

					// Animation speeds
					Vector2 waveSpeeds1 = EditorGUILayout.Vector2Field("Speed 1", new Vector2(displacementSpeed.vectorValue.x, displacementSpeed.vectorValue.y));
					Vector2 waveSpeeds2 = EditorGUILayout.Vector2Field("Speed 2", new Vector2(displacementSpeed.vectorValue.z, displacementSpeed.vectorValue.w));

					displacementSpeed.vectorValue = new Vector4(waveSpeeds1.x, waveSpeeds1.y, waveSpeeds2.x, waveSpeeds2.y);



					m_MaterialEditor.ShaderProperty(smoothing, "Smoothing");
				}
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();
		}

		void DrawTessellation()
        {
			if (_hasTess)
			{
				GUILayout.BeginVertical("GroupBox");
				EditorGUI.indentLevel = 1;
				_ShowTessellation = EditorGUILayout.Foldout(_ShowTessellation, "Tessellation", _FoldoutStyle);

				if (_ShowTessellation)
				{
					m_MaterialEditor.ShaderProperty(tess, "Tessellation Level");
				}

				EditorGUI.indentLevel = 0;
				GUILayout.EndVertical();
			}
		}

		void DrawOptions()
        {
			GUILayout.BeginVertical("GroupBox");
			EditorGUI.indentLevel = 1;
			_ShowOptions = EditorGUILayout.Foldout(_ShowOptions, "Options", _FoldoutStyle);

			if (_ShowOptions)
			{
				m_MaterialEditor.ShaderProperty(worldSpace, "WorldSpace UV");
			}

			EditorGUI.indentLevel = 0;
			GUILayout.EndVertical();

		}

	}
}