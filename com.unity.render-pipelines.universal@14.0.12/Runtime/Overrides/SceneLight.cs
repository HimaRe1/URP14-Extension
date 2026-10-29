using System;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{

    [Serializable, VolumeComponentMenu("Custom/SceneLight")]
    public sealed class SceneLight : VolumeComponent, IPostProcessComponent
    {


        //Ambient

        [Tooltip("AmbientSky")]
        public ColorParameter ambientSky = new ColorParameter(Color.white, true, false, true);

        [Tooltip("AmbientGround")]
        public ColorParameter ambientGround = new ColorParameter(Color.white, true, false, true);

        [Tooltip("AmbientEquator")]
        public ColorParameter ambientEquator = new ColorParameter(Color.white, true, false, true);

        [Tooltip("AmbientCubemap")]
        public CubemapParameter ambientCubemap = new CubemapParameter(null);

        [Tooltip("AmbientCubemapExposure")]
        public MinFloatParameter ambientCubemapExposure = new MinFloatParameter(1f, 0f);


        //fog
        [Tooltip("Fog")]
        public BoolParameter fog = new BoolParameter(false);

        [Tooltip("FogColor")]
        public ColorParameter fogColor = new ColorParameter(Color.white, true, false, true);

        [Tooltip("FogStartDistance")]
        public NoInterpMinFloatParameter startDistance = new NoInterpMinFloatParameter(50f, 0f );

        [Tooltip("FogEndDistance")]
        public NoInterpMinFloatParameter endDistance = new NoInterpMinFloatParameter(70f, 0f);
       

        public bool IsActive() => true;
        
        public bool IsTileCompatible() => false;
    }

   
}


