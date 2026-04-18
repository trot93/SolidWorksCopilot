using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class FilletSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "Fillet", Name = feature.Name };

            var filletData = feature.GetSpecificFeature2() as ISimpleFilletFeatureData2;
            if (filletData != null)
            {
                data.Parameters["radius_mm"] = Math.Round(filletData.DefaultRadius * 1000.0, 4);
                data.Parameters["fillet_type"] = "circular";
            }

            return data;
        }
    }
}