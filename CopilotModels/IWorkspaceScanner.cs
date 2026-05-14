namespace CopilotModels
{
    public interface IWorkspaceScanner
    {
        WorkspaceContext ScanWorkspace(string goal);

        // SPRINT 3: Returns count of non-default features currently in the feature tree.
        // Used for basic "did the step execute?" verification in OnMarkDoneAndNext().
        // Note: GetFeatureTreeHash() and ExtractDimensions() are deferred to Sprint 4.
        int GetFeatureCount();
    }
}