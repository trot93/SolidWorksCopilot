using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CopilotModels;

namespace CopilotAddIn
{
    public class HoleSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "Hole", Name = feature.Name };

            var holeData = feature.GetSpecificFeature2() as IWizardHoleFeatureData2;
            if (holeData != null)
            {
                data.Plane = ExtrudeSerializer.GetSketchPlaneName(feature);
                data.Parameters["hole_type"] = GetHoleTypeString((int)holeData.Type);
                data.Parameters["diameter_mm"] = Math.Round(holeData.Diameter * 1000.0, 4);
                data.Parameters["depth_mm"] = Math.Round(holeData.Depth * 1000.0, 4);
            }

            return data;
        }

        private string GetHoleTypeString(int holeType)
        {
            if (holeType == (int)swWzdHoleTypes_e.swCounterBored ||
                holeType == (int)swWzdHoleTypes_e.swCounterBoreBlind)
                return "counterbore";
            if (holeType == (int)swWzdHoleTypes_e.swCounterSunk ||
                holeType == (int)swWzdHoleTypes_e.swCounterSinkBlind)
                return "countersink";
            if (holeType == (int)swWzdHoleTypes_e.swHoleBlind ||
                holeType == (int)swWzdHoleTypes_e.swHoleThru ||
                holeType == (int)swWzdHoleTypes_e.swSimple)
                return "simple";
            if (holeType == (int)swWzdHoleTypes_e.swTapBlind ||
                holeType == (int)swWzdHoleTypes_e.swTapThru)
                return "tapped";
            if (holeType == (int)swWzdHoleTypes_e.swPipeTapBlind ||
                holeType == (int)swWzdHoleTypes_e.swPipeTapThru)
                return "pipe_tap";
            return $"type_{holeType}";
        }
    }
}