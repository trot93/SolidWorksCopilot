using System;
using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public class ShellSerializer : IFeatureSerializer
    {
        public FeatureData Serialize(Feature feature, int step)
        {
            var data = new FeatureData { Step = step, Type = "Shell", Name = feature.Name };

            var shellData = feature.GetSpecificFeature2() as IShellFeatureData;
            if (shellData != null)
            {
                data.Parameters["thickness_mm"] = Math.Round(shellData.Thickness * 1000.0, 4);
                data.Parameters["face_count"] = shellData.FacesRemovedCount;
            }

            return data;
        }
    }
}