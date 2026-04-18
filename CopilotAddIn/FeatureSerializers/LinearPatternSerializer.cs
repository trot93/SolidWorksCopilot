using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class LinearPatternSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "LinearPattern", Name = feature.Name };

            var patternData = feature.GetSpecificFeature2() as ILinearPatternFeatureData;
            if (patternData != null)
            {
                data.Parameters["spacing_mm"] = Math.Round(patternData.D1Spacing * 1000.0, 4);
                data.Parameters["instances"] = patternData.D1TotalInstances;
                data.Parameters["direction_1"] = patternData.D1Axis ?? "sketch-defined";
            }

            return data;
        }
    }
}