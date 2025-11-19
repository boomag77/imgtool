using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
     public class DespeckleSettings 
    {
        
        public bool SmallAreaRelative=true;
        public double SmallAreaMultiplier=0.25;
        public int SmallAreaAbsolutePx=64;
        public double MaxDotHeightFraction=0.35;
        public double ProximityRadiusFraction=0.8;
        public double SquarenessTolerance=0.6;
        public bool KeepClusters=true;
        public bool UseDilateBeforeCC=true;
        public string DilateKernel="1x3"; // "1x3", "3x1" or "3x3"
        public int DilateIter=1;
        public bool ShowDespeckleDebug = false;
    }

}
