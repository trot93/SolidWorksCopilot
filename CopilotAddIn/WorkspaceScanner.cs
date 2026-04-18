// CHANGES FROM ORIGINAL:
// - FIX: Data models (WorkspaceContext, MaterialInfo, FeatureData) moved to CopilotModels project.
//   Removed local class definitions, added using CopilotModels.
// - All other fixes from previous round retained (meters→mm, document type guard, etc.)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CopilotModels;

namespace CopilotAddIn
{
    public class WorkspaceScanner : CopilotModels.IWorkspaceScanner
    {
        private readonly ISldWorks swApp;
        private readonly CopilotCore.SessionLogger logger;
        private readonly Dictionary<string, IFeatureSerializer> serializers;

        public WorkspaceScanner(ISldWorks app, CopilotCore.SessionLogger log)
        {
            swApp  = app;
            logger = log;

            serializers = new Dictionary<string, IFeatureSerializer>
            {
                ["Boss-Extrude"]  = new ExtrudeSerializer(),
                ["Cut-Extrude"]   = new ExtrudeSerializer(),
                ["Fillet"]        = new FilletSerializer(),
                ["Chamfer"]       = new ChamferSerializer(),
                ["RevolveBase"]   = new RevolveSerializer(),
                ["RevolveCut"]    = new RevolveSerializer(),
                ["Shell"]         = new ShellSerializer(),
                ["Hole"]          = new HoleSerializer(),
                ["LPattern"]      = new LinearPatternSerializer(),
                ["CirPattern"]    = new CircularPatternSerializer()
            };
        }

        public WorkspaceContext ScanWorkspace(string designGoal = null)
        {
            var context = new WorkspaceContext { DesignGoal = designGoal };
            var model   = swApp.ActiveDoc as ModelDoc2;

            if (model == null) return context;

            var docType = (swDocumentTypes_e)model.GetType();
            if (docType == swDocumentTypes_e.swDocDRAWING)
            {
                context.OlderFeaturesSummary = "Active document is a drawing — open a part to use the copilot.";
                return context;
            }
            if (docType == swDocumentTypes_e.swDocASSEMBLY)
            {
                context.OlderFeaturesSummary = "Assembly mode support coming in Phase 2.";
                return context;
            }

            try
            {
                context.Material        = GetMaterialInfo(model);
                context.ActiveSelection = GetActiveSelection(model);

                var featureList = new List<FeatureData>();
                var feature     = model.FirstFeature() as Feature;
                int step        = 1;

                while (feature != null && step <= 30)
                {
                    var featureData = ExtractFeatureData(feature, step);
                    if (featureData != null)
                    {
                        featureList.Add(featureData);
                        step++;
                    }
                    feature = feature.GetNextFeature() as Feature;
                }

                if (feature != null)
                {
                    var remaining = new List<string>();
                    while (feature != null)
                    {
                        remaining.Add(feature.GetTypeName2());
                        feature = feature.GetNextFeature() as Feature;
                    }
                    context.OlderFeaturesSummary =
                        $"{remaining.Count} earlier features (not detailed): " +
                        string.Join(", ", remaining.Take(5));
                }

                context.Features = featureList;
                logger?.LogScan(context);
                return context;
            }
            catch (Exception ex)
            {
                logger?.LogError("Workspace scan failed", ex);
                return context;
            }
        }

        private FeatureData ExtractFeatureData(Feature feature, int step)
        {
            var typeName = feature.GetTypeName2();

            if (serializers.TryGetValue(typeName, out var serializer))
            {
                try
                {
                    return serializer.Serialize(feature, step);
                }
                catch (COMException ex)
                {
                    logger?.LogError($"Failed to serialize feature type: {typeName}", ex);
                    return new FeatureData
                    {
                        Step           = step,
                        Type           = typeName,
                        Name           = feature.Name,
                        UnresolvedType = true,
                        Error          = "Feature cast failed"
                    };
                }
            }

            return new FeatureData
            {
                Step           = step,
                Type           = typeName,
                Name           = feature.Name,
                UnresolvedType = true
            };
        }

        private MaterialInfo GetMaterialInfo(ModelDoc2 model)
        {
            try
            {
                var part = model as PartDoc;
                if (part == null) return null;
                var materialName = part.MaterialUserName;
                var materialProps = part.MaterialPropertyValues as double[];
                return new MaterialInfo
                {
                    Name = materialName,
                    Density = materialProps?[0] ?? 0,
                    YoungsModulus = materialProps?[1] ?? 0,
                    PoissonRatio = materialProps?[2] ?? 0
                };
            }
            catch (COMException)
            {
                return new MaterialInfo { Name = "Material read failed" };
            }
        }

        private string GetActiveSelection(ModelDoc2 model)
        {
            try
            {
                var selMgr = model.SelectionManager as SelectionMgr;
                if (selMgr == null) return null;
                var count  = selMgr.GetSelectedObjectCount2(-1);
                if (count == 0) return null;
                var selected = selMgr.GetSelectedObject6(1, -1);
                return selected?.GetType().Name;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }
}