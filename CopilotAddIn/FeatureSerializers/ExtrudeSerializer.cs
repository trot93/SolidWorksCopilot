using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CopilotModels;

namespace CopilotAddIn
{
    public class ExtrudeSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData
            {
                Step = step,
                Type = feature.GetTypeName2(),
                Name = feature.Name
            };

            var extrudeData = feature.GetSpecificFeature2() as IExtrudeFeatureData2;
            if (extrudeData != null)
            {
                data.Plane = GetSketchPlaneName(feature);
                data.Parameters["depth_mm"] = Math.Round(extrudeData.GetDepth(true) * 1000.0, 4);
                data.Parameters["direction"] = extrudeData.ReverseDirection ? "reverse" : "normal";
                data.Parameters["end_condition"] = GetEndConditionString(extrudeData.GetEndCondition(true));
                data.Parameters["is_cut"] = feature.GetTypeName2().Contains("Cut");
            }

            return data;
        }

        public static string GetSketchPlaneName(Feature feature)
        {
            try
            {
                var sketch = feature.GetSpecificFeature2() as ISketch;
                if (sketch == null) return "Unknown Plane";

                int entityType = 0;
                var entity = sketch.GetReferenceEntity(ref entityType);

                if (entityType == (int)swSelectType_e.swSelREFEDGES ||
                    entityType == (int)swSelectType_e.swSelFACES)
                {
                    var face = entity as Face2;
                    if (face != null)
                    {
                        var feat = face.GetFeature() as Feature;
                        return feat != null ? $"Face of {feat.Name}" : "Planar Face";
                    }
                }

                if (entity is Feature planeFeat)
                    return planeFeat.Name;

                return "Unknown Plane";
            }
            catch (COMException)
            {
                return "Unknown Plane";
            }
        }

        private string GetEndConditionString(int condition)
        {
            return condition switch
            {
                (int)swEndConditions_e.swEndCondBlind => "blind",
                (int)swEndConditions_e.swEndCondThroughAll => "through_all",
                (int)swEndConditions_e.swEndCondUpToSurface => "up_to_surface",
                _ => "unknown"
            };
        }
    }
}