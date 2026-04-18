using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class CircularPatternSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "CircularPattern", Name = feature.Name };

            var patternData = feature.GetSpecificFeature2() as ICircularPatternFeatureData;
            if (patternData != null)
            {
                data.Parameters["angle_deg"] = Math.Round(patternData.Spacing * (180.0 / Math.PI), 2);
                data.Parameters["instances"] = patternData.TotalInstances;
                data.Parameters["axis"] = patternData.Axis ?? "sketch-defined";
            }

            return data;
        }
    }
}