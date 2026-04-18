using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class RevolveSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData
            {
                Step = step,
                Type = feature.GetTypeName2().Contains("Cut") ? "Revolve-Cut" : "Revolve-Base",
                Name = feature.Name
            };

            var revolveData = feature.GetSpecificFeature2() as IRevolveFeatureData2;
            if (revolveData != null)
            {
                data.Plane = ExtrudeSerializer.GetSketchPlaneName(feature);
                data.Parameters["angle_deg"] = Math.Round(revolveData.GetRevolutionAngle(true) * (180.0 / Math.PI), 2);
                data.Parameters["direction"] = revolveData.ReverseDirection ? "reverse" : "normal";
            }

            return data;
        }
    }
}