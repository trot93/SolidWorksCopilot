using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class ChamferSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "Chamfer", Name = feature.Name };

            var chamferData = feature.GetSpecificFeature2() as IChamferFeatureData;
            if (chamferData != null)
            {
                data.Parameters["width_mm"] = Math.Round(chamferData.GetEdgeChamferDistance(0) * 1000.0, 4);
                data.Parameters["angle_deg"] = Math.Round(chamferData.EdgeChamferAngle * (180.0 / Math.PI), 2);
            }

            return data;
        }
    }
}