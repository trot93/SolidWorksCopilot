// CHANGES FROM ORIGINAL: None — interface is correct as-is.

using SolidWorks.Interop.sldworks;
using CopilotModels;

namespace CopilotAddIn
{
    public interface IFeatureSerializer
    {
        FeatureData Serialize(Feature feature, int step);
    }
}
